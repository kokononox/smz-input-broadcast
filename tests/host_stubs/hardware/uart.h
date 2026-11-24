#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
typedef struct uart_inst uart_inst_t;
extern uart_inst_t *uart1;
#define UART_PARITY_NONE 0
void uart_init(uart_inst_t *uart, unsigned int baud);
void uart_set_hw_flow(uart_inst_t *uart, bool cts, bool rts);
void uart_set_format(uart_inst_t *uart, unsigned int data_bits, unsigned int stop_bits, int parity);
void uart_set_fifo_enabled(uart_inst_t *uart, bool enabled);
void uart_write_blocking(uart_inst_t *uart, const uint8_t *src, size_t len);
