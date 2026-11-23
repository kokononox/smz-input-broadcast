# آزمون انتقال فریم رویداد آزمایشی

این مرحله بعد از آزمون PING/ACK ساخته شده تا انتقال **فریم دارای payload** را هم بررسی کند. فقط یک الگوی آزمایشی ثابت و شمارنده فرستاده می‌شود؛ فریمور ورودی واقعی موس/صفحه‌کلید را جمع‌آوری نمی‌کند و هیچ HID یا کلیدی را اجرا نمی‌کند.

## ساخت

در ریشهٔ مخزن و با Pico SDK نصب‌شده، هر نقش را جداگانه بسازید:

```sh
cmake -S firmware/event_transport_test -B build/event-master -DPICO_BOARD=pico -DEVENT_TEST_ROLE=MASTER
cmake --build build/event-master --parallel

cmake -S firmware/event_transport_test -B build/event-client -DPICO_BOARD=pico -DEVENT_TEST_ROLE=CLIENT
cmake --build build/event-client --parallel
```

خروجی‌ها:

- `build/event-master/smz_event_transport_test.uf2`
- `build/event-client/smz_event_transport_test.uf2`

این UF2ها موقتاً جای فریمور فعلی را می‌گیرند؛ پیش از فلش، UF2های فعلی را نگه دارید. MASTER را فقط روی Pico مادر و CLIENT را فقط روی یک Pico کلاینت فلش کنید.

## اتصال و اجرا

با خاموش‌بودن بردها، GP8/TX هر برد را به GP9/RX برد مقابل، GP9/RX را به GP8/TX مقابل، و GND را به GND وصل کنید. GPIOها ۳٫۳ ولتی‌اند؛ ۵ ولت به آن‌ها وصل نکنید. دکمه‌های GP3/GP4 و UART فعلی GP16/GP17 تغییری نمی‌کنند.

پورت USB CDC پیکوی مادر را با مانیتور Windows روی 115200 baud باز کنید. گزارش `MASTER,mode=event-test,...` باید در هر ثانیه حدود ۵۰ فریم و ۵۰ ACK، با timeout و CRC صفر نشان دهد. خروجی COM پیکوی کلاینت `CLIENT,event=...,ack=...,invalid=...,gaps=...,crc=...` را گزارش می‌کند.

RTT زمان رفت‌وبرگشت است، نه تأخیر یک‌طرفه. این آزمون فقط حمل‌ونقل UART و اعتبار payload را می‌سنجد؛ هنوز پل ورودی Windows و اجرای HID را آزمایش نمی‌کند.

## قاب فریم

`A5 5A | version:u8 | type:u8 | sequence:u16le | payload_length:u8 | timestamp_us:u32le | payload | CRC16:u16le`

CRC-16/CCITT روی هدرِ بعد از sync و payload محاسبه می‌شود. اندازهٔ payload در این مرحله حداکثر ۱۶ بایت است. تنها نوع payload حاضر، `TEST_EVENT_KIND=0x7E` با شمارنده و الگوی ثابت است؛ انواع دادهٔ ورودی در این مرحله تعریف یا اجرا نشده‌اند.
