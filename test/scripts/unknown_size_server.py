#!/usr/bin/env python3
"""Serves a fixed-size payload with NO Content-Length header, for manually testing AvaDM's
unknown-size download fallback (Downloader.RunAsync when HEAD returns no Content-Length).

Usage: python3 unknown_size_server.py [total_bytes] [duration_seconds] [port]
Defaults: 20 MB over 8s, port 8765. Ctrl+C to stop.

Add http://127.0.0.1:<port>/payload.bin as a download in AvaDM once it's running.

Keep duration_seconds comfortably under AvaDM's default 30s per-attempt timeout (Settings >
retries, DownloadSettings.DefaultPerAttemptTimeout): this fallback is non-resumable, so a per-
attempt timeout restarts the whole download from byte 0 instead of resuming - expected behavior,
not a bug, but it means a slow-paced payload here can spend several full download attempts just
retrying itself.
"""
import http.server
import socketserver
import sys
import time

SIZE = int(sys.argv[1]) if len(sys.argv) > 1 else 20 * 1024 * 1024
DURATION = float(sys.argv[2]) if len(sys.argv) > 2 else 8.0
PORT = int(sys.argv[3]) if len(sys.argv) > 3 else 8765
CHUNK = 32 * 1024
CHUNK_COUNT = max(1, -(-SIZE // CHUNK))  # ceil division
DELAY = DURATION / CHUNK_COUNT


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send_headers(self):
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Connection", "close")
        # Deliberately no Content-Length - that's the whole point.
        self.end_headers()

    def do_HEAD(self):
        self._send_headers()

    def do_GET(self):
        self._send_headers()
        payload = bytes(i % 256 for i in range(CHUNK))
        sent = 0
        while sent < SIZE:
            n = min(CHUNK, SIZE - sent)
            try:
                self.wfile.write(payload[:n])
            except (BrokenPipeError, ConnectionResetError):
                return
            sent += n
            time.sleep(DELAY)

    def log_message(self, fmt, *args):
        print(f"[{self.address_string()}] {fmt % args}")


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


if __name__ == "__main__":
    with Server(("0.0.0.0", PORT), Handler) as httpd:
        print(f"Serving {SIZE:,} bytes with no Content-Length over ~{DURATION:.1f}s "
              f"at http://127.0.0.1:{PORT}/payload.bin")
        httpd.serve_forever()
