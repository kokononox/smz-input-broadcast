#!/usr/bin/env python3
"""Apply the standalone one-way mirror hook to the pinned SMZ runtime checkout."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

OVERLAY_ROOT = Path(__file__).resolve().parents[1] / "firmware" / "one_way_mirror_overlay"


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"Expected one anchor in {path}, found {count}: {old!r}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="temporary checkout of the pinned upstream source")
    args = parser.parse_args()
    pico = args.source / "firmware" / "abvm" / "pico"
    if not (pico / "main.c").is_file() or not (pico / "guard_runtime.c").is_file():
        raise SystemExit("The source path is not the expected dual-sensor ABVM checkout")

    replace_once(
        pico / "CMakeLists.txt",
        "add_executable(ams_abvm_pico main.c hid_keyboard.c arm_uart_mouse.c light_sensor.c",
        "add_executable(ams_abvm_pico main.c hid_keyboard.c arm_uart_mouse.c mirror_link.c light_sensor.c",
    )
    replace_once(
        pico / "main.c",
        '#include "arm_uart_mouse.h"\n',
        '#include "arm_uart_mouse.h"\n#include "mirror_link.h"\n',
    )
    replace_once(
        pico / "main.c",
        "board_init(); hid_keyboard_init(); arm_uart_mouse_init(); light_sensor_init(now_ms()); buzzer_init();",
        "board_init(); hid_keyboard_init(); arm_uart_mouse_init(); mirror_link_init(); light_sensor_init(now_ms()); buzzer_init();",
    )
    replace_once(
        pico / "main.c",
        "service_keyboard(now); service_mouse(now); service_cycle(now);",
        "service_keyboard(now); service_mouse(now); mirror_link_service(); service_cycle(now);",
    )
    replace_once(
        pico / "main.c",
        "shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u); release_all_actors(now_ms()); buzzer_silence();",
        "shift_identity_forget(&shift_identity);hid_keyboard_set_shift(0u); release_all_actors(now_ms()); buzzer_silence(); mirror_link_release_all();",
    )
    replace_once(
        pico / "main.c",
        "release_all_actors(now_ms()); buzzer_silence();\n    /* `remote_wakeup_en` is the host's answer",
        "release_all_actors(now_ms()); buzzer_silence(); mirror_link_release_all();\n    /* `remote_wakeup_en` is the host's answer",
    )
    replace_once(
        pico / "main.c",
        "void tud_resume_cb(void) {\n    pico_usb_suspended=false;",
        "void tud_resume_cb(void) {\n    pico_usb_suspended=false;\n    mirror_link_release_all();",
    )
    replace_once(
        pico / "hid_keyboard.c",
        '#include "tusb.h"\n',
        '#include "tusb.h"\n#include "mirror_link.h"\n',
    )
    replace_once(
        pico / "hid_keyboard.c",
        "    return tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, keys);",
        "    bool sent = tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, keys);\n"
        "    if (sent) mirror_link_keyboard_report(modifiers, keys);\n"
        "    return sent;",
    )
    replace_once(
        pico / "arm_uart_mouse.c",
        '#include "calibration_store.h"\n',
        '#include "calibration_store.h"\n#include "mirror_link.h"\n',
    )
    replace_once(
        pico / "arm_uart_mouse.c",
        "static bool queue_payload(const char *payload, uint32_t now, ArmState next) {\n"
        "    return queue_frame(payload, now, next, true);\n"
        "}",
        "static bool queue_payload(const char *payload, uint32_t now, ArmState next) {\n"
        "    if (!queue_frame(payload, now, next, true)) return false;\n"
        "    mirror_link_mouse_command(payload);\n"
        "    return true;\n"
        "}",
    )

    for name in ("mirror_link.c", "mirror_link.h"):
        shutil.copy2(OVERLAY_ROOT / name, pico / name)
    print(f"Applied one-way mirror overlay to {pico}")


if __name__ == "__main__":
    main()
