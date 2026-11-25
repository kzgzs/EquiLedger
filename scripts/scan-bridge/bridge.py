#!/usr/bin/env python3
# 玄铁栈道 EquiLedger 扫码桥接器（统一设备适配协议参考实现）
# 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
# 用法: python bridge.py --adapter usb|serial|network|camera|sdk --server http://主机:8090 \
#       --device DEV-01 --secret <操作员领取的密钥> [--port COM3|/dev/ttyUSB0] [--baud 9600] [--listen 9000]
# 协议: POST /api/scan  body={"barcode": "..."}  头 X-Device-Id / X-Device-Ts(unix秒) / X-Device-Sign=HMAC-SHA256(secret, ts+"|"+barcode) hex
# 企业现场具备何种设备/网络/工控环境由企业确认；厂商专有协议不开放接口时需额外开发或更换设备。
import argparse, hashlib, hmac, json, sys, time, urllib.request


def send(server, device, secret, barcode):
    ts = str(int(time.time()))
    sign = hmac.new(secret.encode(), (ts + "|" + barcode).encode(), hashlib.sha256).hexdigest()
    req = urllib.request.Request(
        server.rstrip("/") + "/api/scan",
        data=json.dumps({"barcode": barcode}).encode(),
        headers={"Content-Type": "application/json", "X-Device-Id": device, "X-Device-Ts": ts, "X-Device-Sign": sign},
        method="POST")
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            print("[OK]", r.read().decode())
    except urllib.error.HTTPError as e:
        print("[DENY]", e.code, e.read().decode())
    except Exception as e:
        print("[ERR]", e)


def iter_usb():
    # USB 键盘楔入/HID 串口模拟：扫描枪把条码当键盘输入，一行一条
    for line in sys.stdin:
        line = line.strip()
        if line:
            yield line


def iter_serial(port, baud):
    import serial  # pip install pyserial
    with serial.Serial(port, baud, timeout=1) as s:
        while True:
            line = s.readline().decode(errors="ignore").strip()
            if line:
                yield line


def iter_network(listen):
    import socket
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", listen))
    srv.listen(8)
    print(f"[network] listening :{listen}")
    while True:
        conn, _ = srv.accept()
        with conn:
            buf = b""
            while True:
                chunk = conn.recv(4096)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.decode(errors="ignore").strip()
                    if line:
                        yield line


def iter_camera():
    import cv2  # pip install opencv-python pyzbar
    from pyzbar import pyzbar
    cap = cv2.VideoCapture(0)
    seen = set()
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        for d in pyzbar.decode(frame):
            code = d.data.decode()
            if code not in seen:
                seen.add(code)
                yield code


def iter_sdk():
    # 厂商 SDK/API 适配器挂点：在此调用厂商 SDK 回调/轮询，yield 条码字符串。
    # 厂家不开放接口时无法无条件兼容：需额外开发或更换设备。
    raise SystemExit("sdk 适配器需按厂商文档实现 iter_sdk() 后使用")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--adapter", required=True, choices=["usb", "serial", "network", "camera", "sdk"])
    ap.add_argument("--server", required=True)
    ap.add_argument("--device", required=True)
    ap.add_argument("--secret", required=True)
    ap.add_argument("--port", default="COM3")
    ap.add_argument("--baud", type=int, default=9600)
    ap.add_argument("--listen", type=int, default=9000)
    a = ap.parse_args()
    src = {"usb": iter_usb, "serial": lambda: iter_serial(a.port, a.baud),
           "network": lambda: iter_network(a.listen), "camera": iter_camera, "sdk": iter_sdk}[a.adapter]()
    for barcode in src:
        send(a.server, a.device, a.secret, barcode)


if __name__ == "__main__":
    main()