# Pico-to-Pico link test (test-only)

This prototype checks a one-client UART link and reports request/ACK round-trip time. It sends only `PING|sequence` and `ACK|sequence`; it does **not** capture or replay keyboard/mouse input and does not initialize HID.

## Confirmed pins

The user confirmed GP3/GP4 are used for the physical buttons and GP8/GP9 are free on both Pico boards. GP16/GP17 are already used by the Pico-to-Pro-Micro UART.

For the test pair:

- Mother Pico GP8/TX → client Pico GP9/RX
- Client Pico GP8/TX → mother Pico GP9/RX
- Ground reference between the Pico signal sides

Both Pico GPIO sides are 3.3 V logic. Do not connect the Pro Micro 5 V rail to these GPIOs. The boards are powered by separate computers, so confirm the grounding/isolation arrangement before connecting grounds; if uncertain, stop and use an appropriately isolated/differential link instead of a direct cable.

The target cable length is 0.5–less than 3 m. The configured baud rate is 115200; actual reliability must be measured on the real cable.

## Run

1. Back up each Pico's current `code.py` before testing. These scripts temporarily replace the normal firmware entry point; use bench/test boards if available.
2. Copy `client/code.py` to the client Pico's CIRCUITPY drive and `master/code.py` to the mother Pico's CIRCUITPY drive.
3. Connect the UART signal pair and the verified ground reference; do not connect the 5 V rail.
4. Open the mother Pico's CircuitPython console. It reports statistics every 10 successful replies and prints timeouts.
5. Stop the test and restore the backed-up firmware before reconnecting the Picos to normal SMZ operation.

The reported RTT includes the request, client parsing/ACK, and return path; it is not a one-way measurement. No hardware test has been run from this repository, and these scripts should be treated as a bench prototype.