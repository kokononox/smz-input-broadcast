# Test-only Pico-to-Pico UART link client.
# Replies only to PING frames; it never emits keyboard, mouse, or HID events.
import time
import board
import busio

BAUDRATE = 115200
RX_LIMIT = 256

# RP2040 UART1 pins: GP8=TX, GP9=RX.
link = busio.UART(tx=board.GP8, rx=board.GP9, baudrate=BAUDRATE, timeout=0)
rx_buffer = bytearray()
ping_count = 0

print("Pico link-test client: GP8/GP9 UART1, {} baud; no HID output".format(BAUDRATE))

while True:
    if not link.in_waiting:
        time.sleep(0.001)
        continue

    chunk = link.read(link.in_waiting)
    if not chunk:
        time.sleep(0.001)
        continue
    rx_buffer.extend(chunk)
    if len(rx_buffer) > RX_LIMIT:
        rx_buffer.clear()
        continue

    while True:
        newline = rx_buffer.find(b"\n")
        if newline < 0:
            break
        raw = bytes(rx_buffer[:newline])
        del rx_buffer[:newline + 1]
        try:
            line = raw.decode("ascii").strip()
            prefix, value = line.split("|", 1)
            sequence = int(value)
        except (UnicodeError, ValueError):
            continue

        if prefix == "PING":
            link.write(("ACK|{}\n".format(sequence)).encode("ascii"))
            ping_count += 1
            if ping_count % 100 == 0:
                print("received {} PING frames".format(ping_count))
