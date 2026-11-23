#include <stddef.h>
#include "usb_descriptors.h"

#define REPORT_ID_KEYBOARD 1u
#define REPORT_ID_MOUSE 2u
#define CONFIG_TOTAL_LEN (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN)

static tusb_desc_device_t const device_descriptor = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = 0,
    .bDeviceSubClass = 0,
    .bDeviceProtocol = 0,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = 0xCafe,
    .idProduct = 0x4010,
    .bcdDevice = 0x0100,
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = 3,
    .bNumConfigurations = 1
};

static uint8_t const hid_report_descriptor[] = {
    TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(REPORT_ID_KEYBOARD)),
    /* Mouse report: five buttons, X/Y, vertical wheel, and horizontal pan. */
    0x05, 0x01,             /* Usage Page (Generic Desktop) */
    0x09, 0x02,             /* Usage (Mouse) */
    0xA1, 0x01,             /* Collection (Application) */
    0x85, REPORT_ID_MOUSE,  /*   Report ID */
    0x09, 0x01,             /*   Usage (Pointer) */
    0xA1, 0x00,             /*   Collection (Physical) */
    0x05, 0x09,             /*     Usage Page (Button) */
    0x19, 0x01, 0x29, 0x05, /*     Usage Min/Max (1..5) */
    0x15, 0x00, 0x25, 0x01, /*     Logical Min/Max (0..1) */
    0x95, 0x05, 0x75, 0x01, /*     Report Count 5, Size 1 */
    0x81, 0x02,             /*     Input (Data, Variable, Absolute) */
    0x95, 0x01, 0x75, 0x03, /*     Padding: 3 bits */
    0x81, 0x03,             /*     Input (Constant, Variable, Absolute) */
    0x05, 0x01,             /*     Usage Page (Generic Desktop) */
    0x09, 0x30,             /*     Usage (X) */
    0x09, 0x31,             /*     Usage (Y) */
    0x09, 0x38,             /*     Usage (Wheel) */
    0x15, 0x81, 0x25, 0x7F, /*     Logical Min/Max (-127..127) */
    0x75, 0x08, 0x95, 0x03, /*     Report Size 8, Count 3 */
    0x81, 0x06,             /*     Input (Data, Variable, Relative) */
    0x05, 0x0C,             /*     Usage Page (Consumer) */
    0x0A, 0x38, 0x02,       /*     Usage (AC Pan) */
    0x15, 0x81, 0x25, 0x7F,
    0x75, 0x08, 0x95, 0x01,
    0x81, 0x06,             /*     Input (Data, Variable, Relative) */
    0xC0,                   /*   End Collection */
    0xC0                    /* End Collection */
};

static uint8_t const configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, 1, 0, CONFIG_TOTAL_LEN,
                          TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 100),
    TUD_HID_DESCRIPTOR(0, 0, HID_ITF_PROTOCOL_NONE,
                       sizeof(hid_report_descriptor), 0x81, 16, 1)
};

uint8_t const *tud_descriptor_device_cb(void) {
    return (uint8_t const *)&device_descriptor;
}

uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return configuration_descriptor;
}

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance) {
    return instance == 0 ? hid_report_descriptor : NULL;
}

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t language_id) {
    (void)language_id;
    static uint16_t descriptor[32];
    static char const *const strings[] = {
        "", "SMZ Prototype", "SMZ HID Client", "0001"
    };

    if (index == 0) {
        descriptor[1] = 0x0409;
        descriptor[0] = (uint16_t)((TUSB_DESC_STRING << 8) | 4u);
        return descriptor;
    }
    if (index >= sizeof(strings) / sizeof(strings[0])) return NULL;

    char const *text = strings[index];
    uint8_t count = 0;
    while (text[count] != '\0' && count < 31u) {
        descriptor[1u + count] = (uint8_t)text[count];
        count++;
    }
    descriptor[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2u + 2u * count));
    return descriptor;
}
