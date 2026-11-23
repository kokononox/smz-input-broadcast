# Test-only Pico-to-Pico UART link master.
# Sends PING sequence numbers and measures round-trip ACK time.
# No keyboard, mouse, or HID APIs are used.
import time
import board
import busio

BAUDRATE = 115200
PING_PERIOD_S = 0.100
ACK_TIMEOUT_S = 0.500
RX_LIMIT = 256

# RP2040 UART1 candidate pins: GP8=TX, GP9=RX.
link = busio.UART(tx=board.GP8, rx=board.GP9, baudrate=BAUDRATE, timeout=0)
rx_buffer = bytearray()

sequence = 0
pending_sequence = None
pending_at = 0.0
next_send_at = time.monotonic()
ok_count = 0
timeout_count = 0
min_rtt_ms = None
max_rtt_ms = 0.0
sum_rtt_ms = 0.0

print("Pico link-test master: GP8/GP9 UART1, {} baud; no HID output".format(BAUDRATE))

while True:
    if link.in_waiting:
        chunk = link.read(link.in_waiting)
        if chunk:
            rx_buffer.extend(chunk)
            if len(rx_buffer) > RX_LIMIT:
                rx_buffer.clear()

    now = time.monotonic()
    while True:
        newline = rx_buffer.find(b"\n")
        if newline < 0:
            break
        raw = bytes(rx_buffer[:newline])
        del rx_buffer[:newline + 1]
        try:
            line = raw.decode("ascii").strip()
            prefix, value = line.split("|", 1)
            ack_sequence = int(value)
        except (UnicodeError, ValueError):
            continue

        if prefix == "ACK" and pending_sequence == ack_sequence:
            rtt_ms = (now - pending_at) * 1000.0
            ok_count += 1
            sum_rtt_ms += rtt_ms
            min_rtt_ms = rtt_ms if min_rtt_ms is None else min(min_rtt_ms, rtt_ms)
            max_rtt_ms = max(max_rtt_ms, rtt_ms)
            pending_sequence = None
            next_send_at = now + PING_PERIOD_S
            if ok_count % 10 == 0:
                avg_rtt_ms = sum_rtt_ms / ok_count
                print("ok={} timeout={} RTT min/avg/max={:.2f}/{:.2f}/{:.2f} ms".format(
                    ok_count, timeout_count, min_rtt_ms, avg_rtt_ms, max_rtt_ms
                ))

    now = time.monotonic()
    if pending_sequence is not None and now - pending_at >= ACK_TIMEOUT_S:
        timeout_count += 1
        print("timeout seq={} (timeouts={})".format(pending_sequence, timeout_count))
        pending_sequence = None
        next_send_at = now + PING_PERIOD_S

    if pending_sequence is None and now >= next_send_at:
        sequence += 1
        pending_sequence = sequence
        pending_at = now
        link.write(("PING|{}\n".format(sequence)).encode("ascii"))

    time.sleep(0.001)
