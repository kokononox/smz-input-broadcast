# فریمور تست Master سازگار با Mirror Client

این تصویر آزمایشی برای فرستادن فریم‌های یک‌طرفه‌ای ساخته شده که Mirror Client فعلی دریافت می‌کند؛ پروتکل تست قدیمیِ ACKدار نیست. فریمور فقط با دستور دستی از USB CDC یک حرکت کوچک ماوس می‌فرستد و هیچ کلیک یا ورودی صفحه‌کلید تولید نمی‌کند.

## سیم‌کشی

- GP8/TX برد Master تست → GP9/RX بردی که فریمور Mirror Client روی آن است
- GND دو برد → یکدیگر
- سیم RX برگشتی و اتصال 5V لازم نیست؛ دو برد را از USB خودشان تغذیه کنید.

## اجرا

1. روی برد مقصد، فریمور Mirror Client سازگار را اجرا و با نگه‌داشتن هم‌زمان دکمه‌های فیزیکی Pause/Resume و Start/Stop به‌مدت ۳ ثانیه، حالت Mirror را روشن کنید.
2. فایل `smz_native_mirror_master_test.uf2` را روی برد Master تست فلش کنید.
3. USB برد Master را به رایانه وصل کنید و پورت USB CDC آن را در یک ترمینال سریال باز کنید. سرعت ترمینال برای USB CDC اهمیتی ندارد.
4. با `M` یک حرکت یک‌بارهٔ ۵ پیکسلی به راست، با `N` پنج پیکسل به چپ، یا با `H` فقط heartbeat بفرستید. `?` راهنما را دوباره چاپ می‌کند.

هر دستور حرکتی فقط یک فریم محدود می‌فرستد؛ هیچ ارسال خودکار یا تکرارشونده‌ای وجود ندارد. ابتدا در صفحه‌ای خالی یا محیط آزمایشی تست کنید.

## ساخت

```sh
cmake -S firmware/native_mirror_master_test -B build/native-mirror-master -G Ninja -DPICO_BOARD=pico
cmake --build build/native-mirror-master --parallel 2
```

GitHub Actions نیز پس از push تغییرات این مسیر، فایل UF2 را با نام artifact `SMZ-Native-Mirror-Master-Test-UF2` می‌سازد.
