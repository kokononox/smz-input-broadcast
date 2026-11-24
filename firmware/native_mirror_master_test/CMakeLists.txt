cmake_minimum_required(VERSION 3.13)

if(NOT DEFINED ENV{PICO_SDK_PATH})
    message(FATAL_ERROR "PICO_SDK_PATH must point to a Pico SDK checkout")
endif()

include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)
project(smz_native_mirror_master_test C CXX ASM)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
pico_sdk_init()

# This test image is C-only. Avoid linking the SDK C++ allocation shim.
if(TARGET pico_cxx_options)
    get_target_property(PICO_CXX_OPTION_SOURCES pico_cxx_options INTERFACE_SOURCES)
    if(PICO_CXX_OPTION_SOURCES)
        list(FILTER PICO_CXX_OPTION_SOURCES EXCLUDE REGEX "/new_delete\\.cpp$")
        set_property(TARGET pico_cxx_options PROPERTY INTERFACE_SOURCES "${PICO_CXX_OPTION_SOURCES}")
    endif()
endif()

add_executable(smz_native_mirror_master_test native_mirror_master_test.c)
set_property(TARGET smz_native_mirror_master_test PROPERTY LINKER_LANGUAGE C)
target_compile_options(smz_native_mirror_master_test PRIVATE -Wall -Wextra -Werror)
target_link_libraries(smz_native_mirror_master_test PRIVATE pico_stdlib hardware_uart)
pico_enable_stdio_usb(smz_native_mirror_master_test 1)
pico_enable_stdio_uart(smz_native_mirror_master_test 0)
pico_add_extra_outputs(smz_native_mirror_master_test)
