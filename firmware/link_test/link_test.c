#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "pico/stdlib.h"

#if defined(LINK_TEST_MASTER)
#define ROLE_NAME "MASTER"
#elif defined(LINK_TEST_CLIENT)
#define ROLE_NAME "CLIENT"
#else
#error "Build with LINK_TEST_ROLE=MASTER or LINK_TEST_ROLE=CLIENT"
#endif

#define LINK_UART uart1
#define LINK_BAUD 1000000u
#define LINK_TX_PIN 8u
#define LINK_RX_PIN 9u

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_PING 1u
#define FRAME_TYPE_ACK 2u
#define FRAME_SIZE 12u

#define PING_INTERVAL_US 20000u
#define ACK_TIMEOUT_US 100000u
#define REPORT_INTERVAL_US 1000000u

typedef struct {
    uint8_t bytes[FRAME_SIZE];
    uint8_t count;
    uint32_t crc_errors;
} FrameParser;

typedef struct {
    uint8_t type;
    uint16_t sequence;
    uint32_t timestamp_us;
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

static void make_frame(uint8_t out[FRAME_SIZE], uint8_t type,
                       uint16_t sequence, uint32_t timestamp_us) {
    out[0] = FRAME_SYNC0;
    out[1] = FRAME_SYNC1;
    out[2] = FRAME_VERSION;
    out[3] = type;
    put_u16_le(&out[4], sequence);
    put_u32_le(&out[6], timestamp_us);
    const uint16_t crc = crc16_ccitt(&out[2], 8u);
    put_u16_le(&out[10], crc);
}

static void send_frame(uint8_t type, uint16_t sequence, uint32_t timestamp_us) {
    uint8_t bytes[FRAME_SIZE];
    make_frame(bytes, type, sequence, timestamp_us);
    uart_write_blocking(LINK_UART, bytes, FRAME_SIZE);
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
        } else if (byte != FRAME_SYNC0) {
            parser->count = 0u;
        }
        return false;
    }

    parser->bytes[parser->count++] = byte;
    if (parser->count < FRAME_SIZE) {
        return false;
    }

    parser->count = 0u;
    const uint16_t received_crc = get_u16_le(&parser->bytes[10]);
    const uint16_t calculated_crc = crc16_ccitt(&parser->bytes[2], 8u);
    if (parser->bytes[2] != FRAME_VERSION || received_crc != calculated_crc) {
        parser->crc_errors++;
        return false;
    }

    frame->type = parser->bytes[3];
    frame->sequence = get_u16_le(&parser->bytes[4]);
    frame->timestamp_us = get_u32_le(&parser->bytes[6]);
    return true;
}

static bool read_frame(FrameParser *parser, Frame *frame) {
    bool got_frame = false;
    while (uart_is_readable(LINK_UART)) {
        const uint8_t byte = (uint8_t)uart_getc(LINK_UART);
        if (parser_feed(parser, byte, frame)) {
            got_frame = true;
        }
    }
    return got_frame;
}

static void init_link_uart(void) {
    uart_init(LINK_UART, LINK_BAUD);
    gpio_set_function(LINK_TX_PIN, GPIO_FUNC_UART);
    gpio_set_function(LINK_RX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(LINK_UART, false, false);
    uart_set_format(LINK_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(LINK_UART, true);
}

#ifdef LINK_TEST_MASTER
static void run_master(void) {
    FrameParser parser = {0};
    uint16_t sequence = 0u;
    bool pending = false;
    uint16_t pending_sequence = 0u;
    uint32_t pending_timestamp = 0u;
    uint32_t last_ping_us = time_us_32();
    uint32_t last_report_us = last_ping_us;

    uint32_t pings_sent = 0u;
    uint32_t acks_received = 0u;
    uint32_t timeouts = 0u;
    uint32_t rtt_samples = 0u;
    uint32_t rtt_min_us = UINT32_MAX;
    uint32_t rtt_max_us = 0u;
    uint64_t rtt_sum_us = 0u;

    printf("SMZ UART1 link test: %s, %lu baud, GP%u TX / GP%u RX\n",
           ROLE_NAME, (unsigned long)LINK_BAUD, LINK_TX_PIN, LINK_RX_PIN);

    while (true) {
        Frame frame;
        while (read_frame(&parser, &frame)) {
            if (frame.type == FRAME_TYPE_ACK && pending &&
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

        if (!pending && (uint32_t)(now_us - last_ping_us) >= PING_INTERVAL_US) {
            sequence++;
            pending_sequence = sequence;
            pending_timestamp = now_us;
            last_ping_us = now_us;
            pending = true;
            pings_sent++;
            send_frame(FRAME_TYPE_PING, pending_sequence, pending_timestamp);
        }

        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            const uint32_t average = rtt_samples == 0u
                ? 0u : (uint32_t)(rtt_sum_us / rtt_samples);
            const uint32_t min_value = rtt_samples == 0u ? 0u : rtt_min_us;
            printf("MASTER,tx=%lu,ack=%lu,timeout=%lu,rtt_us_min=%lu,rtt_us_avg=%lu,rtt_us_max=%lu,crc=%lu\n",
                   (unsigned long)pings_sent, (unsigned long)acks_received,
                   (unsigned long)timeouts, (unsigned long)min_value,
                   (unsigned long)average, (unsigned long)rtt_max_us,
                   (unsigned long)parser.crc_errors);
            pings_sent = 0u;
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
#else
static void run_client(void) {
    FrameParser parser = {0};
    uint32_t last_report_us = time_us_32();
    uint32_t pings_received = 0u;
    uint32_t acks_sent = 0u;

    printf("SMZ UART1 link test: %s, %lu baud, GP%u TX / GP%u RX\n",
           ROLE_NAME, (unsigned long)LINK_BAUD, LINK_TX_PIN, LINK_RX_PIN);

    while (true) {
        Frame frame;
        while (read_frame(&parser, &frame)) {
            if (frame.type == FRAME_TYPE_PING) {
                pings_received++;
                send_frame(FRAME_TYPE_ACK, frame.sequence, frame.timestamp_us);
                acks_sent++;
            }
        }

        const uint32_t now_us = time_us_32();
        if ((uint32_t)(now_us - last_report_us) >= REPORT_INTERVAL_US) {
            printf("CLIENT,ping=%lu,ack=%lu,crc=%lu\n",
                   (unsigned long)pings_received, (unsigned long)acks_sent,
                   (unsigned long)parser.crc_errors);
            pings_received = 0u;
            acks_sent = 0u;
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
#ifdef LINK_TEST_MASTER
    run_master();
#else
    run_client();
#endif
    return 0;
}
