#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "pico/stdlib.h"
#include "pico/stdio.h"

#if defined(EVENT_TEST_MASTER)
#define ROLE_NAME "MASTER"
#elif defined(EVENT_TEST_HOST_MASTER)
#define ROLE_NAME "HOST_MASTER"
#elif defined(EVENT_TEST_HOST_MIRROR_MASTER)
#define ROLE_NAME "HOST_MIRROR_MASTER"
#elif defined(EVENT_TEST_CLIENT)
#define ROLE_NAME "CLIENT"
#else
#error "Build with EVENT_TEST_ROLE=MASTER, CLIENT, HOST_MASTER, or HOST_MIRROR"
#endif

#define LINK_UART uart1
#define LINK_BAUD 1000000u
#define LINK_TX_PIN 8u
#define LINK_RX_PIN 9u

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_EVENT 1u
#define FRAME_TYPE_ACK 2u
#define FRAME_HEADER_SIZE 11u
#define FRAME_CRC_SIZE 2u
#define MAX_PAYLOAD_SIZE 16u
#define MAX_FRAME_SIZE (FRAME_HEADER_SIZE + MAX_PAYLOAD_SIZE + FRAME_CRC_SIZE)

#define TEST_EVENT_KIND 0x7Eu
#define TEST_PAYLOAD_SIZE 8u
#define TEST_PATTERN0 0xA5u
#define TEST_PATTERN1 0x5Au
#define TEST_PATTERN2 0xC3u

/* Input event kinds shared by the host bridge and the HID-capable client. */
#define INPUT_EVENT_KIND_KEY 0x01u
#define INPUT_EVENT_KIND_MOUSE_MOVE 0x02u
#define INPUT_EVENT_KIND_MOUSE_BUTTON 0x03u
#define INPUT_EVENT_KIND_MOUSE_WHEEL 0x04u
#define INPUT_EVENT_KIND_HEARTBEAT 0x05u
#define INPUT_EVENT_KIND_STATE_SNAPSHOT 0x07u

#define EVENT_INTERVAL_US 20000u
#define ACK_TIMEOUT_US 100000u
#define REPORT_INTERVAL_US 1000000u

typedef struct {
    uint8_t bytes[MAX_FRAME_SIZE];
    uint8_t count;
    uint8_t expected_size;
    uint32_t crc_errors;
} FrameParser;

typedef struct {
    uint8_t type;
    uint8_t payload_length;
    uint16_t sequence;
    uint32_t timestamp_us;
    uint8_t payload[MAX_PAYLOAD_SIZE];
} Frame;

static uint16_t get_u16_le(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t get_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] |
           ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

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

static void make_frame(uint8_t out[MAX_FRAME_SIZE], uint8_t type,
                       uint16_t sequence, uint32_t timestamp_us,
                       const uint8_t *payload, uint8_t payload_length) {
    out[0] = FRAME_SYNC0;
    out[1] = FRAME_SYNC1;
    out[2] = FRAME_VERSION;
    out[3] = type;
    put_u16_le(&out[4], sequence);
    out[6] = payload_length;
    put_u32_le(&out[7], timestamp_us);
    if (payload_length > 0u) {
        memcpy(&out[FRAME_HEADER_SIZE], payload, payload_length);
    }
    const uint16_t crc = crc16_ccitt(&out[2], 9u + payload_length);
    put_u16_le(&out[FRAME_HEADER_SIZE + payload_length], crc);
}

static void send_frame(uint8_t type, uint16_t sequence, uint32_t timestamp_us,
                       const uint8_t *payload, uint8_t payload_length) {
    uint8_t bytes[MAX_FRAME_SIZE];
    make_frame(bytes, type, sequence, timestamp_us, payload, payload_length);
    uart_write_blocking(LINK_UART, bytes, FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
}

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
            parser->count = 1u;
        } else {
            parser->count = 0u;
        }
        return false;
    }

    parser->bytes[parser->count++] = byte;
    if (parser->count == 7u) {
        const uint8_t payload_length = parser->bytes[6];
        if (payload_length > MAX_PAYLOAD_SIZE) {
            parser->count = 0u;
            parser->expected_size = 0u;
            return false;
        }
        parser->expected_size = (uint8_t)(FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
    }

    if (parser->expected_size == 0u || parser->count < parser->expected_size) {
        return false;
    }

    const uint8_t payload_length = parser->bytes[6];
    const uint16_t received_crc = get_u16_le(&parser->bytes[FRAME_HEADER_SIZE + payload_length]);
    const uint16_t calculated_crc = crc16_ccitt(&parser->bytes[2], 9u + payload_length);
    const bool valid = parser->bytes[2] == FRAME_VERSION && received_crc == calculated_crc;
    parser->count = 0u;
    parser->expected_size = 0u;
    if (!valid) {
        parser->crc_errors++;
        return false;
    }

    frame->type = parser->bytes[3];
    frame->sequence = get_u16_le(&parser->bytes[4]);
    frame->payload_length = payload_length;
    frame->timestamp_us = get_u32_le(&parser->bytes[7]);
    if (payload_length > 0u) {
        memcpy(frame->payload, &parser->bytes[FRAME_HEADER_SIZE], payload_length);
    }
    return true;
}

#if !defined(EVENT_TEST_HOST_MIRROR_MASTER)
static bool read_frame(FrameParser *parser, Frame *frame) {
    while (uart_is_readable(LINK_UART)) {
        const uint8_t byte = (uint8_t)uart_getc(LINK_UART);
        if (parser_feed(parser, byte, frame)) {
            return true;
        }
    }
    return false;
}
#endif

static void init_link_uart(void) {
    uart_init(LINK_UART, LINK_BAUD);
    gpio_set_function(LINK_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(LINK_RX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(LINK_UART, false, false);
    uart_set_format(LINK_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(LINK_UART, true);
}

#if defined(EVENT_TEST_CLIENT) || defined(EVENT_TEST_HOST_MASTER) || defined(EVENT_TEST_HOST_MIRROR_MASTER)
static bool is_valid_event(const Frame *frame) {
    if (frame->type != FRAME_TYPE_EVENT || frame->payload_length == 0u) {
        return false;
    }

    /* Keep the existing deterministic transport-test event for regression tests. */
    if (frame->payload[0] == TEST_EVENT_KIND) {
        return frame->payload_length == TEST_PAYLOAD_SIZE &&
               get_u32_le(&frame->payload[1]) == (uint32_t)frame->sequence &&
               frame->payload[5] == TEST_PATTERN0 &&
               frame->payload[6] == TEST_PATTERN1 &&
               frame->payload[7] == TEST_PATTERN2;
    }

    /* Input-event wire schema. The test client validates only; the HID client may execute when physically armed. */
    switch (frame->payload[0]) {
        case INPUT_EVENT_KIND_KEY:
            /* kind, scan-code LE16, down(1)/up(0), extended(0/1) */
            return frame->payload_length == 5u &&
                   frame->payload[3] <= 1u && frame->payload[4] <= 1u;
        case INPUT_EVENT_KIND_MOUSE_MOVE:
            /* kind, dx LE16, dy LE16 */
            return frame->payload_length == 5u;
        case INPUT_EVENT_KIND_MOUSE_BUTTON:
            /* kind, button(1..5), down(1)/up(0) */
            return frame->payload_length == 3u &&
                   frame->payload[1] >= 1u && frame->payload[1] <= 5u &&
                   frame->payload[2] <= 1u;
        case INPUT_EVENT_KIND_MOUSE_WHEEL:
            /* kind, delta LE16, axis(0 vertical / 1 horizontal) */
            return frame->payload_length == 4u && frame->payload[3] <= 1u;
        case INPUT_EVENT_KIND_HEARTBEAT:
            /* kind only; keeps the armed HID client's link watchdog alive */
            return frame->payload_length == 1u;
        case INPUT_EVENT_KIND_STATE_SNAPSHOT:
            /* kind, modifiers, six HID usages, mouse-button bitmask */
            if (frame->payload_length != 9u || (frame->payload[8] & 0xE0u) != 0u) return false;
            for (unsigned i = 2u; i < 8u; ++i) {
                if (frame->payload[i] >= 0xE0u) return false;
            }
            return true;
        default:
            return false;
    }
}
#endif

#if defined(EVENT_TEST_HOST_MASTER) || defined(EVENT_TEST_HOST_MIRROR_MASTER)
static bool read_host_frame(FrameParser *parser, Frame *frame) {
    while (true) {
        const int value = getchar_timeout_us(0);
        if (value < 0) {
            return false;
        }
        if (parser_feed(parser, (uint8_t)value, frame)) {
            return true;
        }
    }
}
#endif

#ifdef EVENT_TEST_MASTER
static void run_master(void) {
    FrameParser parser = {0};
    uint16_t sequence = 0u;
    bool pending = false;
    uint16_t pending_sequence = 0u;
    uint32_t pending_timestamp = 0u;
    uint32_t last_event_us = time_us_32();
    uint32_t last_report_us = last_event_us;

    uint32_t events_sent = 0u;
    uint32_t acks_received = 0u;
    uint32_t timeouts = 0u;
    uint32_t rtt_samples = 0u;
    uint32_t rtt_min_us = UINT32_MAX;
    uint32_t rtt_max_us = 0u;
    uint64_t rtt_sum_us = 0u;

    printf("SMZ event transport test: %s, UART1 %lu baud, GP%u TX / GP%u RX; no HID\n",
           ROLE_NAME, (unsigned long)LINK_BAUD, LINK_TX_PIN, LINK_RX_PIN);

    while (true) {
        Frame frame;
        while (read_frame(&parser, &frame)) {
            if (frame.type == FRAME_TYPE_ACK && frame.payload_length == 0u && pending &&
                frame.sequence == pending_sequence &&
                frame.timestamp_us == pending_timestamp) {
                const uint32_t rtt_us = time_us_32() - pending_timestamp;
                pending = false;
                acks_received++;
                rtt_samples++;
                rtt_sum_us += rtt_us;
                if (rtt_us < rtt_min_us) rtt_min_us = rtt_us;
                if (rtt_us > rtt_max_us) rtt_max_us = rtt_us;
            }
        }

        const uint32_t now_us = time_us_32();
        if (pending && (uint32_t)(now_us - pending_timestamp) >= ACK_TIMEOUT_US) {
            pending = false;
            timeouts++;
        }

        if (!pending && (uint32_t)(now_us - last_event_us) >= EVENT_INTERVAL_US) {
            uint8_t payload[TEST_PAYLOAD_SIZE] = {
                TEST_EVENT_KIND, 0u, 0u, 0u, 0u,
                TEST_PATTERN0, TEST_PATTERN1, TEST_PATTERN2
            };
            sequence++;
            put_u32_le(&payload[1], (uint32_t)sequence);
            pending_sequence = sequence;
            pending_timestamp = now_us;
            last_event_us = now_us;
            pending = true;
            events_sent++;
            send_frame(FRAME_TYPE_EVENT, pending_sequence, pending_timestamp,
                       payload, TEST_PAYLOAD_SIZE);
        }

        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            const uint32_t average = rtt_samples == 0u
                ? 0u : (uint32_t)(rtt_sum_us / rtt_samples);
            const uint32_t min_value = rtt_samples == 0u ? 0u : rtt_min_us;
            printf("MASTER,mode=event-test,tx=%lu,ack=%lu,timeout=%lu,rtt_us_min=%lu,rtt_us_avg=%lu,rtt_us_max=%lu,crc=%lu\n",
                   (unsigned long)events_sent, (unsigned long)acks_received,
                   (unsigned long)timeouts, (unsigned long)min_value,
                   (unsigned long)average, (unsigned long)rtt_max_us,
                   (unsigned long)parser.crc_errors);
            events_sent = 0u;
            acks_received = 0u;
            timeouts = 0u;
            rtt_samples = 0u;
            rtt_min_us = UINT32_MAX;
            rtt_max_us = 0u;
            rtt_sum_us = 0u;
            last_report_us = now_us;
        }
        tight_loop_contents();
    }
}
#elif defined(EVENT_TEST_HOST_MASTER)
static void run_host_master(void) {
    FrameParser uart_parser = {0};
    FrameParser host_parser = {0};
    bool pending = false;
    uint16_t pending_sequence = 0u;
    uint32_t pending_timestamp = 0u;
    uint32_t pending_started_us = 0u;
    uint32_t last_report_us = time_us_32();

    uint32_t host_frames = 0u;
    uint32_t events_sent = 0u;
    uint32_t acks_received = 0u;
    uint32_t timeouts = 0u;
    uint32_t invalid_events = 0u;
    uint32_t busy_drops = 0u;
    uint32_t rtt_samples = 0u;
    uint32_t rtt_min_us = UINT32_MAX;
    uint32_t rtt_max_us = 0u;
    uint64_t rtt_sum_us = 0u;

    printf("SMZ host event bridge test: %s, USB CDC -> UART1 %lu baud, no input capture or HID\n",
           ROLE_NAME, (unsigned long)LINK_BAUD);

    while (true) {
        Frame frame;
        while (read_frame(&uart_parser, &frame)) {
            if (frame.type == FRAME_TYPE_ACK && frame.payload_length == 0u && pending &&
                frame.sequence == pending_sequence &&
                frame.timestamp_us == pending_timestamp) {
                const uint32_t rtt_us = time_us_32() - pending_started_us;
                pending = false;
                acks_received++;
                rtt_samples++;
                rtt_sum_us += rtt_us;
                if (rtt_us < rtt_min_us) rtt_min_us = rtt_us;
                if (rtt_us > rtt_max_us) rtt_max_us = rtt_us;
            }
        }

        while (read_host_frame(&host_parser, &frame)) {
            if (!is_valid_event(&frame)) {
                invalid_events++;
                continue;
            }
            host_frames++;
            if (pending) {
                busy_drops++;
                continue;
            }
            pending = true;
            pending_sequence = frame.sequence;
            pending_timestamp = frame.timestamp_us;
            pending_started_us = time_us_32();
            events_sent++;
            send_frame(FRAME_TYPE_EVENT, frame.sequence, frame.timestamp_us,
                       frame.payload, frame.payload_length);
        }

        const uint32_t now_us = time_us_32();
        if (pending && (uint32_t)(now_us - pending_started_us) >= ACK_TIMEOUT_US) {
            pending = false;
            timeouts++;
        }

        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            const uint32_t average = rtt_samples == 0u
                ? 0u : (uint32_t)(rtt_sum_us / rtt_samples);
            const uint32_t min_value = rtt_samples == 0u ? 0u : rtt_min_us;
            printf("MASTER,mode=host-test,host_rx=%lu,tx=%lu,ack=%lu,timeout=%lu,busy=%lu,invalid=%lu,rtt_us_min=%lu,rtt_us_avg=%lu,rtt_us_max=%lu,crc=%lu,host_crc=%lu\n",
                   (unsigned long)host_frames, (unsigned long)events_sent,
                   (unsigned long)acks_received, (unsigned long)timeouts,
                   (unsigned long)busy_drops, (unsigned long)invalid_events,
                   (unsigned long)min_value, (unsigned long)average,
                   (unsigned long)rtt_max_us, (unsigned long)uart_parser.crc_errors,
                   (unsigned long)host_parser.crc_errors);
            host_frames = 0u;
            events_sent = 0u;
            acks_received = 0u;
            timeouts = 0u;
            invalid_events = 0u;
            busy_drops = 0u;
            rtt_samples = 0u;
            rtt_min_us = UINT32_MAX;
            rtt_max_us = 0u;
            rtt_sum_us = 0u;
            last_report_us = now_us;
        }
        tight_loop_contents();
    }
}
#elif defined(EVENT_TEST_HOST_MIRROR_MASTER)
static void run_host_mirror_master(void) {
    FrameParser host_parser = {0};
    uint32_t last_report_us = time_us_32();
    uint32_t host_frames = 0u;
    uint32_t events_sent = 0u;
    uint32_t invalid_events = 0u;

    printf("SMZ one-way mirror bridge: %s, USB CDC -> UART1 %lu baud, GP%u TX -> client RX fan-out; ACK disabled\n",
           ROLE_NAME, (unsigned long)LINK_BAUD, LINK_TX_PIN);

    while (true) {
        Frame frame;
        while (read_host_frame(&host_parser, &frame)) {
            if (!is_valid_event(&frame)) {
                invalid_events++;
                continue;
            }

            /* Broadcast the same validated event to every client RX connected
             * to this TX line. No client response is required or consumed. */
            send_frame(FRAME_TYPE_EVENT, frame.sequence, frame.timestamp_us,
                       frame.payload, frame.payload_length);
            host_frames++;
            events_sent++;
        }

        const uint32_t now_us = time_us_32();
        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            printf("MASTER,mode=mirror,host_rx=%lu,tx=%lu,ack=0,timeout=0,busy=0,invalid=%lu,host_crc=%lu\n",
                   (unsigned long)host_frames, (unsigned long)events_sent,
                   (unsigned long)invalid_events,
                   (unsigned long)host_parser.crc_errors);
            host_frames = 0u;
            events_sent = 0u;
            invalid_events = 0u;
            last_report_us = now_us;
        }
        tight_loop_contents();
    }
}
#else
static void run_client(void) {
    FrameParser parser = {0};
    uint32_t last_report_us = time_us_32();
    uint32_t events_received = 0u;
    uint32_t acks_sent = 0u;
    uint32_t invalid_events = 0u;
    uint32_t sequence_gaps = 0u;
    uint16_t last_sequence = 0u;
    bool have_sequence = false;

    printf("SMZ event transport test: %s, UART1 %lu baud, GP%u TX / GP%u RX; no HID\n",
           ROLE_NAME, (unsigned long)LINK_BAUD, LINK_TX_PIN, LINK_RX_PIN);

    while (true) {
        Frame frame;
        while (read_frame(&parser, &frame)) {
            if (!is_valid_event(&frame)) {
                invalid_events++;
                continue;
            }
            if (have_sequence && frame.sequence != (uint16_t)(last_sequence + 1u)) {
                sequence_gaps++;
            }
            last_sequence = frame.sequence;
            have_sequence = true;
            events_received++;
            send_frame(FRAME_TYPE_ACK, frame.sequence, frame.timestamp_us, NULL, 0u);
            acks_sent++;
        }

        const uint32_t now_us = time_us_32();
        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            printf("CLIENT,event=%lu,ack=%lu,invalid=%lu,gaps=%lu,crc=%lu\n",
                   (unsigned long)events_received, (unsigned long)acks_sent,
                   (unsigned long)invalid_events, (unsigned long)sequence_gaps,
                   (unsigned long)parser.crc_errors);
            events_received = 0u;
            acks_sent = 0u;
            invalid_events = 0u;
            sequence_gaps = 0u;
            last_report_us = now_us;
        }
        tight_loop_contents();
    }
}
#endif

int main(void) {
    stdio_init_all();
    init_link_uart();
    sleep_ms(1500u);
#ifdef EVENT_TEST_MASTER
    run_master();
#elif defined(EVENT_TEST_HOST_MASTER)
    run_host_master();
#elif defined(EVENT_TEST_HOST_MIRROR_MASTER)
    run_host_mirror_master();
#else
    run_client();
#endif
    return 0;
}
