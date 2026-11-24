#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "hardware/uart.h"
#include "mirror_link.h"
struct uart_inst { int unused; };
static struct uart_inst test_uart;
uart_inst_t *uart1 = &test_uart;
static uint8_t wire[4096];
static size_t wire_len;
static uint32_t fake_now;
void gpio_set_function(unsigned int pin, unsigned int fn) {(void)pin;(void)fn;}
void uart_init(uart_inst_t *u, unsigned int baud) {(void)u; assert(baud==1000000u);}
void uart_set_hw_flow(uart_inst_t *u, bool cts, bool rts) {(void)u;(void)cts;(void)rts;}
void uart_set_format(uart_inst_t *u, unsigned int bits, unsigned int stops, int parity) {(void)u;assert(bits==8&&stops==1&&parity==0);}
void uart_set_fifo_enabled(uart_inst_t *u, bool enabled) {(void)u;(void)enabled;}
void uart_write_blocking(uart_inst_t *u, const uint8_t *src, size_t len) {(void)u; assert(wire_len+len<=sizeof(wire));memcpy(wire+wire_len,src,len);wire_len+=len;}
uint32_t time_us_32(void) {return fake_now;}
static uint16_t crc(const uint8_t *p,size_t n){uint16_t c=0xffff;for(size_t i=0;i<n;i++){c^=(uint16_t)p[i]<<8;for(unsigned b=0;b<8;b++)c=(c&0x8000)?(uint16_t)((c<<1)^0x1021):(uint16_t)(c<<1);}return c;}
static unsigned count_kind(uint8_t kind){size_t at=0;unsigned count=0;while(at<wire_len){assert(wire_len-at>=13);assert(wire[at]==0xa5&&wire[at+1]==0x5a);uint8_t n=wire[at+6];size_t total=13u+n;assert(at+total<=wire_len);uint16_t got=(uint16_t)wire[at+11+n]|((uint16_t)wire[at+12+n]<<8);assert(got==crc(&wire[at+2],9u+n));if(wire[at+11]==kind)count++;at+=total;}assert(at==wire_len);return count;}
int main(void){
    fake_now=1000; mirror_link_init();
    const uint8_t keys[6]={0x04,0,0,0,0,0};
    mirror_link_keyboard_report(0x02,keys);
    assert(count_kind(0x07)==1);
    assert(wire[11]==0x07&&wire[12]==0x02&&wire[13]==0x04);
    mirror_link_mouse_command("MMOVE|12,-4,rel,2");
    assert(count_kind(0x02)==1);
    mirror_link_mouse_command("MDOWN|left");
    mirror_link_mouse_command("MUP|left");
    assert(count_kind(0x03)==2);
    mirror_link_mouse_command("MWHEEL|-1");
    assert(count_kind(0x04)==1);
    mirror_link_mouse_command("MCLICK|right,2,20,20");
    fake_now+=20000;mirror_link_service();
    fake_now+=50000;mirror_link_service();
    fake_now+=20000;mirror_link_service();
    assert(count_kind(0x03)>=6);
    mirror_link_release_all();
    assert(count_kind(0x07)>=2);
    puts("mirror protocol host test passed");
    return 0;
}
