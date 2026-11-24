#include "mirror_link.h"

#include <limits.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "pico/stdlib.h"

/* One-way fan-out: master GP8/TX -> every client GP9/RX, common GND.
 * GP9/RX on the master is intentionally left unclaimed; no client ACK path. */
#define MIRROR_UART uart1
#define MIRROR_BAUD 1000000u
#define MIRROR_TX_PIN 8u
#define MIRROR_FRAME_SYNC0 0xA5u
#define MIRROR_FRAME_SYNC1 0x5Au
#define MIRROR_FRAME_VERSION 1u
#define MIRROR_FRAME_EVENT 1u
#define MIRROR_HEADER_SIZE 11u
#define MIRROR_MAX_PAYLOAD 16u
#define MIRROR_SNAPSHOT_INTERVAL_US 100000u
#define MIRROR_CLICK_GAP_US 50000u

#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_MOUSE_BUTTON 0x03u
#define EVENT_MOUSE_WHEEL 0x04u
#define EVENT_STATE_SNAPSHOT 0x07u

typedef struct {
    bool active;
    bool down;
    uint8_t button;
    uint8_t remaining;
    uint16_t hold_ms;
    uint32_t due_us;
} ClickState;

static uint16_t sequence_number;
static uint8_t keyboard_modifiers;
static uint8_t keyboard_keys[6];
static uint8_t mouse_buttons;
static uint32_t last_snapshot_us;
static bool initialized;
static bool have_absolute_position;
static int32_t absolute_x;
static int32_t absolute_y;
static ClickState click_state;

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

static uint16_t crc16_ccitt(const uint8_t *data, size_t length) {
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

static void send_event(const uint8_t *payload, uint8_t payload_length) {
    if (!initialized || payload_length == 0u || payload_length > MIRROR_MAX_PAYLOAD) return;
    uint8_t frame[MIRROR_HEADER_SIZE + MIRROR_MAX_PAYLOAD + 2u];
    frame[0] = MIRROR_FRAME_SYNC0;
    frame[1] = MIRROR_FRAME_SYNC1;
    frame[2] = MIRROR_FRAME_VERSION;
    frame[3] = MIRROR_FRAME_EVENT;
    ++sequence_number;
    put_u16_le(&frame[4], sequence_number);
    frame[6] = payload_length;
    put_u32_le(&frame[7], time_us_32());
    memcpy(&frame[MIRROR_HEADER_SIZE], payload, payload_length);
    put_u16_le(&frame[MIRROR_HEADER_SIZE + payload_length],
               crc16_ccitt(&frame[2], 9u + payload_length));
    uart_write_blocking(MIRROR_UART, frame,
                        MIRROR_HEADER_SIZE + payload_length + 2u);
}

static void send_snapshot(void) {
    uint8_t payload[9] = { EVENT_STATE_SNAPSHOT, keyboard_modifiers, 0, 0, 0, 0, 0, 0, mouse_buttons };
    memcpy(&payload[2], keyboard_keys, sizeof(keyboard_keys));
    send_event(payload, sizeof(payload));
    last_snapshot_us = time_us_32();
}

static uint8_t button_from_name(const char *text) {
    if (!text) return 0u;
    if (!strcmp(text, "left")) return 1u;
    if (!strcmp(text, "right")) return 2u;
    if (!strcmp(text, "middle")) return 3u;
    if (!strcmp(text, "back")) return 4u;
    if (!strcmp(text, "forward")) return 5u;
    char *end = NULL;
    long value = strtol(text, &end, 10);
    return end != text && *end == '\0' && value >= 1 && value <= 5
        ? (uint8_t)value : 0u;
}

static bool parse_long(const char **cursor, long *value) {
    if (!cursor || !*cursor || !value) return false;
    char *end = NULL;
    long parsed = strtol(*cursor, &end, 10);
    if (end == *cursor) return false;
    *value = parsed;
    *cursor = end;
    return true;
}

static int32_t clamp_i32(long value, int32_t low, int32_t high) {
    if (value < (long)low) return low;
    if (value > (long)high) return high;
    return (int32_t)value;
}

static void send_mouse_move(int64_t dx, int64_t dy) {
    int32_t x = dx < -32767 ? -32767 : (dx > 32767 ? 32767 : (int32_t)dx);
    int32_t y = dy < -32767 ? -32767 : (dy > 32767 ? 32767 : (int32_t)dy);
    if (x == 0 && y == 0) return;
    uint8_t payload[5] = { EVENT_MOUSE_MOVE, 0, 0, 0, 0 };
    put_u16_le(&payload[1], (uint16_t)(int16_t)x);
    put_u16_le(&payload[3], (uint16_t)(int16_t)y);
    send_event(payload, sizeof(payload));
}

static void set_mouse_button(uint8_t button, bool down) {
    if (button < 1u || button > 5u) return;
    uint8_t mask = (uint8_t)(1u << (button - 1u));
    uint8_t next = down ? (uint8_t)(mouse_buttons | mask)
                        : (uint8_t)(mouse_buttons & (uint8_t)~mask);
    if (next == mouse_buttons) return;
    mouse_buttons = next;
    uint8_t payload[3] = { EVENT_MOUSE_BUTTON, button, down ? 1u : 0u };
    send_event(payload, sizeof(payload));
}

static void stop_click(void) {
    if (!click_state.active) return;
    if (click_state.down) set_mouse_button(click_state.button, false);
    memset(&click_state, 0, sizeof(click_state));
}

static void start_click(uint8_t button, uint8_t count, uint16_t hold_ms) {
    stop_click();
    if (!button || !count) return;
    if (count > 8u) count = 8u;
    if (hold_ms < 10u) hold_ms = 10u;
    if (hold_ms > 1000u) hold_ms = 1000u;
    click_state.active = true;
    click_state.down = true;
    click_state.button = button;
    click_state.remaining = count;
    click_state.hold_ms = hold_ms;
    set_mouse_button(button, true);
    click_state.due_us = time_us_32() + (uint32_t)hold_ms * 1000u;
}

static void release_everything(void) {
    stop_click();
    for (uint8_t button = 1u; button <= 5u; ++button)
        set_mouse_button(button, false);
    have_absolute_position = false;
}

static void mirror_move(const char *args) {
    const char *cursor = args;
    long x, y;
    if (!parse_long(&cursor, &x) || *cursor++ != ',' || !parse_long(&cursor, &y)) return;
    while (*cursor == ' ' || *cursor == '\t') ++cursor;
    bool absolute = strncmp(cursor, "abs", 3u) == 0;
    bool relative = strncmp(cursor, "rel", 3u) == 0;
    if (absolute) {
        int32_t next_x = clamp_i32(x, INT32_MIN, INT32_MAX);
        int32_t next_y = clamp_i32(y, INT32_MIN, INT32_MAX);
        if (have_absolute_position)
            send_mouse_move((int64_t)next_x - absolute_x, (int64_t)next_y - absolute_y);
        absolute_x = next_x;
        absolute_y = next_y;
        have_absolute_position = true;
    } else {
        if (relative) have_absolute_position = false;
        send_mouse_move(x, y);
    }
}

static void mirror_wheel(const char *args) {
    const char *cursor = args;
    long delta;
    if (!parse_long(&cursor, &delta)) return;
    if (delta > 273 || delta < -273) delta = delta > 0 ? 273 : -273;
    int16_t wheel_120ths = (int16_t)(delta * 120);
    if (!wheel_120ths) return;
    uint8_t payload[4] = { EVENT_MOUSE_WHEEL, 0, 0, 0 };
    put_u16_le(&payload[1], (uint16_t)wheel_120ths);
    payload[3] = 0u; /* vertical wheel */
    send_event(payload, sizeof(payload));
}

static void mirror_click(const char *args) {
    char button_text[12];
    size_t n = 0u;
    while (args[n] && args[n] != ',' && n + 1u < sizeof(button_text)) {
        button_text[n] = args[n];
        ++n;
    }
    button_text[n] = '\0';
    if (args[n] != ',') return;
    uint8_t button = button_from_name(button_text);
    const char *cursor = args + n + 1u;
    long count = 1, hold_min = 30, hold_max = 90;
    if (!parse_long(&cursor, &count)) return;
    if (*cursor == ',') {
        ++cursor;
        if (!parse_long(&cursor, &hold_min)) return;
        if (*cursor == ',') {
            ++cursor;
            if (!parse_long(&cursor, &hold_max)) return;
        }
    }
    if (count < 1) return;
    if (count > 8) count = 8;
    if (hold_min < 10) hold_min = 10;
    if (hold_max < hold_min) hold_max = hold_min;
    if (hold_max > 1000) hold_max = 1000;
    uint16_t hold = (uint16_t)(hold_min + (hold_max - hold_min) / 2);
    start_click(button, (uint8_t)count, hold);
}

static void mirror_button_command(const char *command, const char *prefix, bool down) {
    size_t prefix_length = strlen(prefix);
    if (strncmp(command, prefix, prefix_length) != 0) return;
    uint8_t button = button_from_name(command + prefix_length);
    if (!button) return;
    stop_click();
    set_mouse_button(button, down);
}

void mirror_link_init(void) {
    uart_init(MIRROR_UART, MIRROR_BAUD);
    gpio_set_function(MIRROR_TX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(MIRROR_UART, false, false);
    uart_set_format(MIRROR_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(MIRROR_UART, true);
    sequence_number = 0u;
    keyboard_modifiers = 0u;
    memset(keyboard_keys, 0, sizeof(keyboard_keys));
    mouse_buttons = 0u;
    memset(&click_state, 0, sizeof(click_state));
    have_absolute_position = false;
    initialized = true;
    last_snapshot_us = time_us_32() - MIRROR_SNAPSHOT_INTERVAL_US;
}

void mirror_link_release_all(void) {
    if (!initialized) return;
    keyboard_modifiers = 0u;
    memset(keyboard_keys, 0, sizeof(keyboard_keys));
    release_everything();
    send_snapshot();
}

void mirror_link_keyboard_report(uint8_t modifiers, const uint8_t keys[6]) {
    if (!initialized || !keys) return;
    if (keyboard_modifiers == modifiers && memcmp(keyboard_keys, keys, sizeof(keyboard_keys)) == 0) return;
    keyboard_modifiers = modifiers;
    memcpy(keyboard_keys, keys, sizeof(keyboard_keys));
    send_snapshot();
}

void mirror_link_mouse_command(const char *command) {
    if (!initialized || !command) return;
    if (!strcmp(command, "HALT") || !strcmp(command, "HALT|SILENT")) {
        release_everything();
        send_snapshot();
        return;
    }
    if (!strncmp(command, "MMOVE|", 6u)) {
        mirror_move(command + 6u);
    } else if (!strncmp(command, "MWHEEL|", 7u)) {
        mirror_wheel(command + 7u);
    } else if (!strncmp(command, "MCLICK|", 7u)) {
        mirror_click(command + 7u);
    } else if (!strncmp(command, "MDOWN|", 6u)) {
        mirror_button_command(command, "MDOWN|", true);
    } else if (!strncmp(command, "MUP|", 4u)) {
        mirror_button_command(command, "MUP|", false);
    }
}

void mirror_link_service(void) {
    if (!initialized) return;
    uint32_t now = time_us_32();
    if (click_state.active && (int32_t)(now - click_state.due_us) >= 0) {
        if (click_state.down) {
            set_mouse_button(click_state.button, false);
            click_state.down = false;
            if (click_state.remaining > 0u) --click_state.remaining;
            if (click_state.remaining == 0u) {
                click_state.active = false;
            } else {
                click_state.due_us = now + MIRROR_CLICK_GAP_US;
            }
        } else {
            set_mouse_button(click_state.button, true);
            click_state.down = true;
            click_state.due_us = now + (uint32_t)click_state.hold_ms * 1000u;
        }
    }
    if ((uint32_t)(now - last_snapshot_us) >= MIRROR_SNAPSHOT_INTERVAL_US)
        send_snapshot();
}
