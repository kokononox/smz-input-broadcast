#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "bsp/board_api.h"
#include "pico/stdlib.h"
#include "tusb.h"

#define LINK_UART uart1
#define LINK_BAUD 1000000u
#define LINK_TX_PIN 8u
#define LINK_RX_PIN 9u
#define ARM_PIN 14u
#define LED_PIN 25u

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_EVENT 1u
#define FRAME_TYPE_ACK 2u
#ifndef HID_CLIENT_SEND_ACK
#define HID_CLIENT_SEND_ACK 1
#endif
#define FRAME_HEADER_SIZE 11u
#define FRAME_CRC_SIZE 2u
#define MAX_PAYLOAD_SIZE 16u
#define MAX_FRAME_SIZE (FRAME_HEADER_SIZE + MAX_PAYLOAD_SIZE + FRAME_CRC_SIZE)

#define EVENT_KEY 0x01u
#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_MOUSE_BUTTON 0x03u
#define EVENT_MOUSE_WHEEL 0x04u
#define EVENT_HEARTBEAT 0x05u
#define TEST_EVENT_KIND 0x7Eu
#define TEST_PAYLOAD_SIZE 8u
#define TEST_PATTERN0 0xA5u
#define TEST_PATTERN1 0x5Au
#define TEST_PATTERN2 0xC3u

#define KEYBOARD_REPORT_ID 1u
#define MOUSE_REPORT_ID 2u
#define LINK_TIMEOUT_US 350000u
#define ARM_DEBOUNCE_US 50000u

typedef struct {
    uint8_t bytes[MAX_FRAME_SIZE];
    uint8_t count;
    uint8_t expected_size;
} FrameParser;

typedef struct {
    uint8_t type;
    uint8_t payload_length;
    uint16_t sequence;
    uint32_t timestamp_us;
    uint8_t payload[MAX_PAYLOAD_SIZE];
} Frame;

static bool armed;
static bool key_down[256];
static uint8_t modifiers;
static uint8_t mouse_buttons;
static bool keyboard_dirty;
static bool mouse_dirty;
static int32_t pending_dx;
static int32_t pending_dy;
static int32_t pending_vertical_wheel;
static int32_t pending_horizontal_wheel;
static uint32_t last_valid_event_us;
static bool link_timed_out;

static uint16_t get_u16_le(uint8_t const *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t get_u32_le(uint8_t const *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

#if HID_CLIENT_SEND_ACK
static void put_u16_le(uint8_t *p, uint16_t value) {
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
}

static void put_u32_le(uint8_t *p, uint32_t value) {
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
    p[2] = (uint8_t)(value >> 16);
    p[3] = (uint8_t)(value >> 24);
}
#endif

static uint16_t crc16_ccitt(uint8_t const *data, size_t length) {
    uint16_t crc = 0xFFFFu;
    for (size_t i = 0; i < length; ++i) {
        crc ^= (uint16_t)data[i] << 8;
        for (uint8_t bit = 0; bit < 8u; ++bit) {
            crc = (crc & 0x8000u) != 0u
                ? (uint16_t)((crc << 1) ^ 0x1021u)
                : (uint16_t)(crc << 1);
        }
    }
    return crc;
}

#if HID_CLIENT_SEND_ACK
static void send_frame(uint8_t type, uint16_t sequence, uint32_t timestamp_us,
                       uint8_t const *payload, uint8_t payload_length) {
    uint8_t bytes[MAX_FRAME_SIZE];
    bytes[0] = FRAME_SYNC0;
    bytes[1] = FRAME_SYNC1;
    bytes[2] = FRAME_VERSION;
    bytes[3] = type;
    put_u16_le(&bytes[4], sequence);
    bytes[6] = payload_length;
    put_u32_le(&bytes[7], timestamp_us);
    if (payload_length != 0u) memcpy(&bytes[FRAME_HEADER_SIZE], payload, payload_length);
    uint16_t crc = crc16_ccitt(&bytes[2], 9u + payload_length);
    put_u16_le(&bytes[FRAME_HEADER_SIZE + payload_length], crc);
    uart_write_blocking(LINK_UART, bytes,
                        FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
}
#endif

static bool parser_feed(FrameParser *parser, uint8_t byte, Frame *frame) {
    if (parser->count == 0u) {
        if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
            parser->count = 1u;
        }
        return false;
    }
    if (parser->count == 1u) {
        if (byte == FRAME_SYNC1) {
            parser->bytes[1] = byte;
            parser->count = 2u;
        } else if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
        } else {
            parser->count = 0u;
        }
        return false;
    }

    parser->bytes[parser->count++] = byte;
    if (parser->count == 7u) {
        uint8_t payload_length = parser->bytes[6];
        if (payload_length > MAX_PAYLOAD_SIZE) {
            parser->count = 0u;
            parser->expected_size = 0u;
            return false;
        }
        parser->expected_size = (uint8_t)(FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
    }
    if (parser->expected_size == 0u || parser->count < parser->expected_size) return false;

    uint8_t payload_length = parser->bytes[6];
    uint16_t received_crc = get_u16_le(&parser->bytes[FRAME_HEADER_SIZE + payload_length]);
    uint16_t calculated_crc = crc16_ccitt(&parser->bytes[2], 9u + payload_length);
    bool valid = parser->bytes[2] == FRAME_VERSION && received_crc == calculated_crc;
    parser->count = 0u;
    parser->expected_size = 0u;
    if (!valid) return false;

    frame->type = parser->bytes[3];
    frame->sequence = get_u16_le(&parser->bytes[4]);
    frame->payload_length = payload_length;
    frame->timestamp_us = get_u32_le(&parser->bytes[7]);
    if (payload_length != 0u) memcpy(frame->payload, &parser->bytes[FRAME_HEADER_SIZE], payload_length);
    return true;
}

static bool read_frame(FrameParser *parser, Frame *frame) {
    while (uart_is_readable(LINK_UART)) {
        if (parser_feed(parser, (uint8_t)uart_getc(LINK_UART), frame)) return true;
    }
    return false;
}

static bool valid_event(Frame const *frame) {
    if (frame->type != FRAME_TYPE_EVENT || frame->payload_length == 0u) return false;
    if (frame->payload[0] == TEST_EVENT_KIND) {
        return frame->payload_length == TEST_PAYLOAD_SIZE &&
               get_u32_le(&frame->payload[1]) == (uint32_t)frame->sequence &&
               frame->payload[5] == TEST_PATTERN0 &&
               frame->payload[6] == TEST_PATTERN1 &&
               frame->payload[7] == TEST_PATTERN2;
    }
    switch (frame->payload[0]) {
        case EVENT_KEY:
            return frame->payload_length == 5u &&
                   get_u16_le(&frame->payload[1]) <= 0xFFu &&
                   frame->payload[3] <= 1u && frame->payload[4] <= 1u;
        case EVENT_MOUSE_MOVE:
            return frame->payload_length == 5u;
        case EVENT_MOUSE_BUTTON:
            return frame->payload_length == 3u &&
                   frame->payload[1] >= 1u && frame->payload[1] <= 5u &&
                   frame->payload[2] <= 1u;
        case EVENT_MOUSE_WHEEL:
            return frame->payload_length == 4u && frame->payload[3] <= 1u;
        case EVENT_HEARTBEAT:
            return frame->payload_length == 1u;
        default:
            return false;
    }
}

/* Windows low-level hook scan codes are translated from set-1 to USB HID usages. */
static uint8_t map_scan_code(uint8_t code, bool extended) {
    if (extended) {
        switch (code) {
            case 0x1C: return 0x58; /* keypad Enter */
            case 0x1D: return 0xE4; /* right Control */
            case 0x35: return 0x54; /* keypad Divide */
            case 0x38: return 0xE6; /* right Alt */
            case 0x47: return 0x4A; /* Home */
            case 0x48: return 0x52; /* Up */
            case 0x49: return 0x4B; /* Page Up */
            case 0x4B: return 0x50; /* Left */
            case 0x4D: return 0x4F; /* Right */
            case 0x4F: return 0x4D; /* End */
            case 0x50: return 0x51; /* Down */
            case 0x51: return 0x4E; /* Page Down */
            case 0x52: return 0x49; /* Insert */
            case 0x53: return 0x4C; /* Delete */
            case 0x5B: return 0xE3; /* left GUI */
            case 0x5C: return 0xE7; /* right GUI */
            case 0x5D: return 0x65; /* Application/Menu */
            default: return 0;
        }
    }
    if (code >= 0x02u && code <= 0x0Bu) {
        static uint8_t const digits[] = {0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27};
        return digits[code - 0x02u];
    }
    if (code >= 0x10u && code <= 0x19u) {
        static uint8_t const row[] = {0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13};
        return row[code - 0x10u];
    }
    if (code >= 0x1Eu && code <= 0x26u) {
        static uint8_t const row[] = {0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F};
        return row[code - 0x1Eu];
    }
    if (code >= 0x2Cu && code <= 0x32u) {
        static uint8_t const row[] = {0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10};
        return row[code - 0x2Cu];
    }
    if (code >= 0x3Bu && code <= 0x44u) return (uint8_t)(0x3Au + code - 0x3Bu);
    switch (code) {
        case 0x01: return 0x29; /* Escape */
        case 0x0C: return 0x2D; case 0x0D: return 0x2E;
        case 0x0E: return 0x2A; case 0x0F: return 0x2B;
        case 0x1A: return 0x2F; case 0x1B: return 0x30;
        case 0x1C: return 0x28; case 0x1D: return 0xE0;
        case 0x27: return 0x33; case 0x28: return 0x34; case 0x29: return 0x35;
        case 0x2A: return 0xE1; case 0x2B: return 0x31;
        case 0x33: return 0x36; case 0x34: return 0x37; case 0x35: return 0x38;
        case 0x36: return 0xE5; case 0x37: return 0x55; case 0x38: return 0xE2;
        case 0x39: return 0x2C; case 0x3A: return 0x39;
        case 0x45: return 0x53; case 0x46: return 0x47;
        case 0x47: return 0x5F; case 0x48: return 0x60; case 0x49: return 0x61;
        case 0x4A: return 0x56; case 0x4B: return 0x5C; case 0x4C: return 0x5D;
        case 0x4D: return 0x5E; case 0x4E: return 0x57;
        case 0x4F: return 0x59; case 0x50: return 0x5A; case 0x51: return 0x5B;
        case 0x52: return 0x62; case 0x53: return 0x63;
        case 0x57: return 0x44; case 0x58: return 0x45;
        default: return 0;
    }
}

static void mark_release_all(void) {
    memset(key_down, 0, sizeof(key_down));
    modifiers = 0;
    mouse_buttons = 0;
    pending_dx = 0;
    pending_dy = 0;
    pending_vertical_wheel = 0;
    pending_horizontal_wheel = 0;
    keyboard_dirty = true;
    mouse_dirty = true;
}

static void update_arm_state(bool new_state, uint32_t now_us) {
    if (armed == new_state) return;
    armed = new_state;
    mark_release_all();
    last_valid_event_us = now_us;
    link_timed_out = false;
}

static void handle_key(uint8_t const *payload) {
    uint8_t code = (uint8_t)get_u16_le(&payload[1]);
    uint8_t usage = map_scan_code(code, payload[4] != 0u);
    if (usage == 0u) return;
    bool down = payload[3] != 0u;
    if (usage >= 0xE0u && usage <= 0xE7u) {
        uint8_t mask = (uint8_t)(1u << (usage - 0xE0u));
        uint8_t next = down ? (uint8_t)(modifiers | mask) : (uint8_t)(modifiers & (uint8_t)~mask);
        if (next != modifiers) {
            modifiers = next;
            keyboard_dirty = true;
        }
    } else if (key_down[usage] != down) {
        if (down) {
            uint8_t count = 0;
            for (unsigned i = 1; i < 256; ++i) if (key_down[i]) count++;
            if (count >= 6u) return;
        }
        key_down[usage] = down;
        keyboard_dirty = true;
    }
}

static int32_t clamp_add(int32_t current, int32_t amount, int32_t limit) {
    int64_t sum = (int64_t)current + amount;
    if (sum > limit) return limit;
    if (sum < -limit) return -limit;
    return (int32_t)sum;
}

static void process_event(Frame const *frame) {
    uint8_t const *p = frame->payload;
    switch (p[0]) {
        case EVENT_KEY:
            handle_key(p);
            break;
        case EVENT_MOUSE_MOVE: {
            int16_t dx = (int16_t)get_u16_le(&p[1]);
            int16_t dy = (int16_t)get_u16_le(&p[3]);
            pending_dx = clamp_add(pending_dx, dx, 32767);
            pending_dy = clamp_add(pending_dy, dy, 32767);
            mouse_dirty = true;
            break;
        }
        case EVENT_MOUSE_BUTTON: {
            uint8_t mask = (uint8_t)(1u << (p[1] - 1u));
            uint8_t next = p[2] != 0u ? (uint8_t)(mouse_buttons | mask) : (uint8_t)(mouse_buttons & (uint8_t)~mask);
            if (next != mouse_buttons) {
                mouse_buttons = next;
                mouse_dirty = true;
            }
            break;
        }
        case EVENT_MOUSE_WHEEL: {
            int16_t delta = (int16_t)get_u16_le(&p[1]);
            if (p[3] == 0u) pending_vertical_wheel = clamp_add(pending_vertical_wheel, delta, 12000);
            else pending_horizontal_wheel = clamp_add(pending_horizontal_wheel, delta, 12000);
            mouse_dirty = true;
            break;
        }
        default:
            break; /* Heartbeat keeps the watchdog alive but has no HID effect. */
    }
}

static int8_t take_mouse_chunk(int32_t *pending) {
    int32_t chunk = *pending;
    if (chunk > 127) chunk = 127;
    if (chunk < -127) chunk = -127;
    *pending -= chunk;
    return (int8_t)chunk;
}

static int8_t take_wheel_step(int32_t *pending) {
    int32_t steps = *pending / 120;
    if (steps > 127) steps = 127;
    if (steps < -127) steps = -127;
    *pending -= steps * 120;
    return (int8_t)steps;
}

static void service_hid(void) {
    if (!tud_mounted() || !tud_hid_ready()) return;
    if (keyboard_dirty) {
        uint8_t keycodes[6] = {0};
        uint8_t count = 0;
        for (unsigned usage = 1; usage < 0xE0u && count < 6u; ++usage) {
            if (key_down[usage]) keycodes[count++] = (uint8_t)usage;
        }
        if (tud_hid_keyboard_report(KEYBOARD_REPORT_ID, modifiers, keycodes)) {
            keyboard_dirty = false;
            return;
        }
    }
    if (mouse_dirty || pending_dx != 0 || pending_dy != 0 ||
        pending_vertical_wheel >= 120 || pending_vertical_wheel <= -120 ||
        pending_horizontal_wheel >= 120 || pending_horizontal_wheel <= -120) {
        int8_t dx = take_mouse_chunk(&pending_dx);
        int8_t dy = take_mouse_chunk(&pending_dy);
        int8_t vertical = take_wheel_step(&pending_vertical_wheel);
        int8_t horizontal = take_wheel_step(&pending_horizontal_wheel);
        if (tud_hid_mouse_report(MOUSE_REPORT_ID, mouse_buttons, dx, dy, vertical, horizontal)) {
            mouse_dirty = false;
        } else {
            pending_dx += dx;
            pending_dy += dy;
            pending_vertical_wheel += (int32_t)vertical * 120;
            pending_horizontal_wheel += (int32_t)horizontal * 120;
        }
    }
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                               hid_report_type_t report_type, uint8_t *buffer,
                               uint16_t requested_length) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)requested_length;
    return 0u; /* No feature/input report is exposed over control GET_REPORT. */
}

void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const *buffer, uint16_t buffer_size) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)buffer_size; /* Keyboard LEDs are intentionally ignored. */
}

void tud_umount_cb(void) {
    mark_release_all();
}

void tud_suspend_cb(bool remote_wakeup_enabled) {
    (void)remote_wakeup_enabled;
    mark_release_all();
}

void tud_resume_cb(void) {
    mark_release_all();
}

int main(void) {
    board_init();
    gpio_init(ARM_PIN);
    gpio_set_dir(ARM_PIN, GPIO_IN);
    gpio_pull_up(ARM_PIN);
    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);
    gpio_put(LED_PIN, false);

    uart_init(LINK_UART, LINK_BAUD);
#if HID_CLIENT_SEND_ACK
    gpio_set_function(LINK_TX_PIN, GPIO_FUNC_UART);
#endif
    gpio_set_function(LINK_RX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(LINK_UART, false, false);
    uart_set_format(LINK_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(LINK_UART, true);

    tusb_rhport_init_t rh_init = {
        .role = TUSB_ROLE_DEVICE,
        .speed = TUSB_SPEED_FULL
    };
    if (!tud_rhport_init(BOARD_TUD_RHPORT, &rh_init)) {
        while (true) tight_loop_contents();
    }
    board_init_after_tusb();
    FrameParser parser = {0};
    uint32_t now_us = time_us_32();
    uint32_t raw_change_us = now_us;
    bool raw_arm_low = !gpio_get(ARM_PIN);
    bool stable_arm_low = false;
    last_valid_event_us = now_us;
    bool mounted_previous = false;
    uint32_t last_led_us = now_us;
    bool led_state = false;

    while (true) {
        tud_task();
        now_us = time_us_32();

        bool current_raw_low = !gpio_get(ARM_PIN);
        if (current_raw_low != raw_arm_low) {
            raw_arm_low = current_raw_low;
            raw_change_us = now_us;
        } else if ((uint32_t)(now_us - raw_change_us) >= ARM_DEBOUNCE_US &&
                   stable_arm_low != raw_arm_low) {
            stable_arm_low = raw_arm_low;
            update_arm_state(stable_arm_low, now_us);
        }

        bool mounted = tud_mounted();
        if (mounted && !mounted_previous) mark_release_all();
        mounted_previous = mounted;

        Frame frame;
        while (read_frame(&parser, &frame)) {
            if (!valid_event(&frame)) continue;
            last_valid_event_us = time_us_32();
            link_timed_out = false;
            if (armed && frame.payload[0] != EVENT_HEARTBEAT) process_event(&frame);
#if HID_CLIENT_SEND_ACK
            send_frame(FRAME_TYPE_ACK, frame.sequence, frame.timestamp_us, NULL, 0u);
#endif
        }

        now_us = time_us_32();
        if (armed && !link_timed_out &&
            (uint32_t)(now_us - last_valid_event_us) > LINK_TIMEOUT_US) {
            mark_release_all();
            link_timed_out = true;
        }

        service_hid();
        if ((uint32_t)(now_us - last_led_us) >= 150000u) {
            last_led_us = now_us;
            if (!armed) {
                led_state = false;
            } else if (mounted) {
                led_state = true;
            } else {
                led_state = !led_state;
            }
            gpio_put(LED_PIN, led_state);
        }
        tight_loop_contents();
    }
}
