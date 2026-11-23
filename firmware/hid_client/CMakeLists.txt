cmake_minimum_required(VERSION 3.13)

if(NOT DEFINED ENV{PICO_SDK_PATH})
    message(FATAL_ERROR "PICO_SDK_PATH must point to a Pico SDK checkout")
endif()

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)
project(smz_hid_client C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
pico_sdk_init()

add_executable(smz_hid_client
    hid_client.c
    usb_descriptors.c
)

target_include_directories(smz_hid_client PRIVATE ${CMAKE_CURRENT_LIST_DIR})
target_compile_options(smz_hid_client PRIVATE -Wall -Wextra -Werror)
target_link_libraries(smz_hid_client PRIVATE pico_stdlib hardware_uart tinyusb_device tinyusb_board)

pico_enable_stdio_usb(smz_hid_client 0)
pico_enable_stdio_uart(smz_hid_client 0)
pico_add_extra_outputs(smz_hid_client)
