# Native Pico-to-Pico UART1 link test

This is a temporary **native C / Pico SDK** firmware test. It sends only heartbeat packets and acknowledgements; it does not capture PC input or emit USB HID events. No Python process runs on the real-time path.

## Wiring

With both boards unpowered, connect:

| Master Pico | Client Pico |
| --- | --- |
| GP8 / UART1 TX | GP9 / UART1 RX |
| GP9 / UART1 RX | GP8 / UART1 TX |
| GND | GND |

Both sides use 3.3 V UART logic at 1,000,000 baud. Do not connect GP8 to GP8 or GP9 to GP9. GP3/GP4 buttons and GP16/GP17 ARM UART are untouched.

## Build

Install the Pico SDK and set `PICO_SDK_PATH`. Build each role in a separate directory:

```sh
cmake -S firmware/link_test -B build/master -DPICO_BOARD=pico -DLINK_TEST_ROLE=MASTER
cmake --build build/master --parallel

cmake -S firmware/link_test -B build/client -DPICO_BOARD=pico -DLINK_TEST_ROLE=CLIENT
cmake --build build/client --parallel
```

The UF2 files are `build/master/smz_input_link_test.uf2` and `build/client/smz_input_link_test.uf2`. Flash the MASTER image only to the master Pico and the CLIENT image only to one client Pico. This test image replaces the currently installed program while it is being tested; re-flash the saved production UF2 afterward.

## Reading the test

Open each Pico's USB CDC serial port at 115200 baud. The UART1 link itself runs at 1 Mbaud. The master reports one-second counts and the full **round-trip** time (RTT) in microseconds. RTT includes both UART transmissions and firmware turnaround; it is not a direct one-way measurement. The client reports received PING and sent ACK counts. Continuous ACKs and zero CRC errors indicate a healthy link.

A PING is sent about every 20 ms when the previous request has completed. A missing ACK times out after 100 ms. CRC-16/CCITT protects the diagnostic frame against accidental corruption; this test protocol is not an encryption scheme and carries no keyboard or mouse data.

## Frame

Each fixed 12-byte frame is `A5 5A | version | type | sequence:u16le | timestamp_us:u32le | crc16:u16le`. Types are `1=PING` and `2=ACK`. The client echoes the sequence and timestamp in the ACK. The master matches both before accepting an RTT sample.
