/**
 * CryptoDecision Mobile — Server Configuration
 *
 * Kết nối thẳng vào API qua port forwarding (không qua nginx).
 * Router: external port 8080 → 192.168.1.2:8080 (api container)
 * API có CORS mở sẵn — /api/ và /hubs/ đều accessible trực tiếp.
 *
 * Để đổi server: sửa SERVER_URL bên dưới rồi rebuild APK.
 */
window.SERVER_URL = "http://123.20.59.4:8080";
