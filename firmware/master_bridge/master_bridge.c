#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "pico/stdio.h"
#include "pico/stdlib.h"

#define LINK_UART uart1
#define LINK_BAUD 1000000u
#define LINK_TX_PIN 8u
#define LED_PIN 25u

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_EVENT 1u
#define FRAME_HEADER_SIZE 11u
#define FRAME_CRC_SIZE 2u
#define MAX_PAYLOAD_SIZE 16u
#define MAX_FRAME_SIZE (FRAME_HEADER_SIZE + MAX_PAYLOAD_SIZE + FRAME_CRC_SIZE)

#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_MOUSE_BUTTON 0x03u
#define EVENT_MOUSE_WHEEL 0x04u
#define EVENT_HEARTBEAT 0x05u
#define EVENT_STATE_SNAPSHOT 0x07u

typedef struct {
    uint8_t bytes[MAX_FRAME_SIZE];
    uint8_t count;
    uint8_t expected_size;
    uint32_t crc_errors;
    uint32_t rejected_events;
} FrameParser;

static uint16_t get_u16_le(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint16_t crc16_ccitt(const uint8_t *data, size_t length) {
    uint16_t crc = 0xFFFFu;
    for (size_t i = 0u; i < length; ++i) {
        crc ^= (uint16_t)data[i] << 8;
        for (uint8_t bit = 0u; bit < 8u; ++bit) {
            crc = (crc & 0x8000u) != 0u
                ? (uint16_t)((crc << 1) ^ 0x1021u)
                : (uint16_t)(crc << 1);
        }
    }
    return crc;
}

static bool valid_event(const uint8_t *payload, uint8_t length) {
    if (payload == NULL || length == 0u) return false;
    switch (payload[0]) {
        case EVENT_MOUSE_MOVE:
            return length == 5u;
        case EVENT_MOUSE_BUTTON:
            return length == 3u && payload[1] >= 1u && payload[1] <= 5u &&
                   payload[2] <= 1u;
        case EVENT_MOUSE_WHEEL:
            return length == 4u && payload[3] <= 1u;
        case EVENT_HEARTBEAT:
            return length == 1u;
        case EVENT_STATE_SNAPSHOT:
            if (length != 9u || (payload[8] & 0xE0u) != 0u) return false;
            for (unsigned i = 2u; i < 8u; ++i) {
                if (payload[i] >= 0xE0u) return false;
            }
            return true;
        default:
            return false;
    }
}

static void parser_reset(FrameParser *parser) {
    parser->count = 0u;
    parser->expected_size = 0u;
}

static void parser_feed(FrameParser *parser, uint8_t byte) {
    if (parser->count == 0u) {
        if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
            parser->count = 1u;
        }
        return;
    }

    if (parser->count == 1u) {
        if (byte == FRAME_SYNC1) {
            parser->bytes[1] = byte;
            parser->count = 2u;
        } else if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
        } else {
            parser_reset(parser);
        }
        return;
    }

    if (parser->count >= MAX_FRAME_SIZE) {
        parser_reset(parser);
        return;
    }
    parser->bytes[parser->count++] = byte;

    if (parser->count == 7u) {
        const uint8_t payload_length = parser->bytes[6];
        if (payload_length > MAX_PAYLOAD_SIZE) {
            parser_reset(parser);
            return;
        }
        parser->expected_size =
            (uint8_t)(FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
    }
    if (parser->expected_size == 0u ||
        parser->count < parser->expected_size) return;

    const uint8_t payload_length = parser->bytes[6];
    const uint16_t received_crc =
        get_u16_le(&parser->bytes[FRAME_HEADER_SIZE + payload_length]);
    const uint16_t calculated_crc =
        crc16_ccitt(&parser->bytes[2], 9u + payload_length);
    const bool valid = parser->bytes[2] == FRAME_VERSION &&
                       parser->bytes[3] == FRAME_TYPE_EVENT &&
                       received_crc == calculated_crc;
    if (!valid) {
        parser->crc_errors++;
        parser_reset(parser);
        return;
    }
    if (!valid_event(&parser->bytes[FRAME_HEADER_SIZE], payload_length)) {
        parser->rejected_events++;
        parser_reset(parser);
        return;
    }

    /* Preserve the validated frame bytes; the client is one-way and sends no ACK. */
    uart_write_blocking(LINK_UART, parser->bytes, parser->expected_size);
    gpio_put(LED_PIN, true);
    parser_reset(parser);
}

int main(void) {
    stdio_init_all();

    uart_init(LINK_UART, LINK_BAUD);
    gpio_set_function(LINK_TX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(LINK_UART, false, false);
    uart_set_format(LINK_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(LINK_UART, true);

    gpio_init(LED_PIN);
    gpio_set_dir(LED_PIN, GPIO_OUT);
    gpio_put(LED_PIN, false);

    FrameParser parser = {0};
    uint32_t led_on_until = 0u;
    while (true) {
        bool received = false;
        int value;
        while ((value = getchar_timeout_us(0u)) >= 0) {
            parser_feed(&parser, (uint8_t)value);
            received = true;
        }
        const uint32_t now = time_us_32();
        if (received) led_on_until = now + 20000u;
        if ((int32_t)(now - led_on_until) >= 0) gpio_put(LED_PIN, false);
        tight_loop_contents();
    }
}
