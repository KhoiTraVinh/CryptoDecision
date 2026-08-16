// Web dashboard — no SERVER_URL override needed (nginx proxies /api/ and /hubs/ internally)
// If you run a local dev server (port 5500, 8000), it fallbacks to the API container on port 8080
if (window.location.port !== '' && window.location.port !== '80' && window.location.port !== '443') {
    window.SERVER_URL = 'http://localhost:8080';
} else {
    window.SERVER_URL = '';
}
