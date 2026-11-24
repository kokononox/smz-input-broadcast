cmake_minimum_required(VERSION 3.13)

if(NOT DEFINED ENV{PICO_SDK_PATH})
    message(FATAL_ERROR "PICO_SDK_PATH must point to a Pico SDK checkout")
endif()

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)
project(smz_hid_client C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
option(HID_CLIENT_SEND_ACK "Send UART acknowledgements to the master" ON)
pico_sdk_init()

# The firmware is C-only. Avoid linking the SDK C++ new/delete shim, which
# requires the separate ARM libstdc++ package and is unused by this target.
if(TARGET pico_cxx_options)
    get_target_property(PICO_CXX_OPTION_SOURCES pico_cxx_options INTERFACE_SOURCES)
    if(PICO_CXX_OPTION_SOURCES)
        list(FILTER PICO_CXX_OPTION_SOURCES EXCLUDE REGEX "/new_delete\\.cpp$")
        set_property(TARGET pico_cxx_options PROPERTY INTERFACE_SOURCES "${PICO_CXX_OPTION_SOURCES}")
    endif()
endif()

add_executable(smz_hid_client
    hid_client.c
    usb_descriptors.c
)

target_include_directories(smz_hid_client PRIVATE ${CMAKE_CURRENT_LIST_DIR})
if(HID_CLIENT_SEND_ACK)
    target_compile_definitions(smz_hid_client PRIVATE HID_CLIENT_SEND_ACK=1)
else()
    target_compile_definitions(smz_hid_client PRIVATE HID_CLIENT_SEND_ACK=0)
endif()
target_compile_options(smz_hid_client PRIVATE -Wall -Wextra -Werror)
target_link_libraries(smz_hid_client PRIVATE pico_stdlib hardware_uart tinyusb_device tinyusb_board)

pico_enable_stdio_usb(smz_hid_client 0)
pico_enable_stdio_uart(smz_hid_client 0)
pico_add_extra_outputs(smz_hid_client)
