#ifndef CLASSROOM_MIRROR_LINK_H
#define CLASSROOM_MIRROR_LINK_H

#include <stdint.h>

void mirror_link_init(void);
void mirror_link_service(void);
void mirror_link_release_all(void);
void mirror_link_keyboard_report(uint8_t modifiers, const uint8_t keys[6]);
void mirror_link_mouse_command(const char *command);

#endif
