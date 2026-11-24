#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#include "hardware/gpio.h"
#include "hardware/uart.h"
#include "pico/stdio.h"
#include "pico/stdlib.h"

#define LINK_UART uart1
#define LINK_BAUD 1000000u
#define LINK_TX_PIN 8u

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_EVENT 1u
#define FRAME_HEADER_SIZE 11u
#define FRAME_CRC_SIZE 2u

#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_HEARTBEAT 0x05u

static uint16_t next_sequence;

static void put_u16_le(uint8_t *dst, uint16_t value) {
    dst[0] = (uint8_t)value;
    dst[1] = (uint8_t)(value >> 8);
}

static void put_u32_le(uint8_t *dst, uint32_t value) {
    dst[0] = (uint8_t)value;
    dst[1] = (uint8_t)(value >> 8);
    dst[2] = (uint8_t)(value >> 16);
    dst[3] = (uint8_t)(value >> 24);
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

static void send_event(const uint8_t *payload, uint8_t payload_length) {
    uint8_t frame[FRAME_HEADER_SIZE + 9u + FRAME_CRC_SIZE];
    const uint16_t sequence = ++next_sequence;
    const uint32_t timestamp_us = time_us_32();

    frame[0] = FRAME_SYNC0;
    frame[1] = FRAME_SYNC1;
    frame[2] = FRAME_VERSION;
    frame[3] = FRAME_TYPE_EVENT;
    put_u16_le(&frame[4], sequence);
    frame[6] = payload_length;
    put_u32_le(&frame[7], timestamp_us);
    for (uint8_t i = 0u; i < payload_length; ++i) {
        frame[FRAME_HEADER_SIZE + i] = payload[i];
    }
    const uint16_t crc = crc16_ccitt(&frame[2], 9u + payload_length);
    put_u16_le(&frame[FRAME_HEADER_SIZE + payload_length], crc);
    uart_write_blocking(LINK_UART, frame,
                        FRAME_HEADER_SIZE + payload_length + FRAME_CRC_SIZE);
}

static void send_mouse_step(int16_t dx) {
    const uint16_t encoded_dx = (uint16_t)dx;
    const uint8_t payload[5] = {
        EVENT_MOUSE_MOVE,
        (uint8_t)(encoded_dx & 0xFFu),
        (uint8_t)(encoded_dx >> 8),
        0u,
        0u
    };
    send_event(payload, sizeof(payload));
    printf("Sent one bounded mouse step: dx=%d, dy=0 (sequence=%u)\n",
           (int)dx, (unsigned)next_sequence);
}

static void send_heartbeat(void) {
    const uint8_t payload[1] = { EVENT_HEARTBEAT };
    send_event(payload, sizeof(payload));
    printf("Sent one heartbeat (sequence=%u)\n", (unsigned)next_sequence);
}

static void show_help(void) {
    printf("Commands: M = move +5 px, N = move -5 px, H = heartbeat, ? = help\n");
    printf("This test image never sends clicks or keyboard input.\n");
}

static void handle_command(int command) {
    switch (command) {
        case 'M':
        case 'm':
            send_mouse_step(5);
            break;
        case 'N':
        case 'n':
            send_mouse_step(-5);
            break;
        case 'H':
        case 'h':
            send_heartbeat();
            break;
        case '?':
            show_help();
            break;
        case '\r':
        case '\n':
        case ' ':
        case '\t':
            break;
        default:
            printf("Unknown command. Press ? for help.\n");
            break;
    }
}

int main(void) {
    stdio_init_all();
    uart_init(LINK_UART, LINK_BAUD);
    gpio_set_function(LINK_TX_PIN, GPIO_FUNC_UART);
    uart_set_hw_flow(LINK_UART, false, false);
    uart_set_format(LINK_UART, 8u, 1u, UART_PARITY_NONE);
    uart_set_fifo_enabled(LINK_UART, true);

    sleep_ms(1500u);
    printf("SMZ native mirror test master ready; USB CDC -> UART1 %lu baud, GP%u TX\n",
           (unsigned long)LINK_BAUD, (unsigned)LINK_TX_PIN);
    printf("No ACK required. Connect GP8 TX to receiver GP9 RX and share GND.\n");
    show_help();

    while (true) {
        const int command = getchar_timeout_us(0u);
        if (command >= 0) handle_command(command);
        tight_loop_contents();
    }
}
