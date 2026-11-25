# 扫码桥接器（统一设备适配协议）

EquiLedger 只负责标准接入能力；企业负责确认现场设备/网络/电脑/工控条件。

## 协议
- `POST /api/scan`，body `{"barcode":"EQL1|类型|业务包号|校验码"}`
- 头：`X-Device-Id`、`X-Device-Ts`（unix 秒，±300s）、`X-Device-Sign` = HMAC-SHA256(设备密钥, `ts|barcode`) 十六进制小写
- 设备密钥：操作员注册设备后在「扫码采集」页领取（服务端生成，不入事件链）
- 返回：Accepted / Duplicate / InvalidCheck / Quarantined / DeviceDenied / BadSign …

## 适配器（bridge.py --adapter）
| 适配器 | 现场形态 | 依赖 |
|---|---|---|
| usb | USB 扫描枪键盘楔入（一行一条码） | 无 |
| serial | 串口扫描枪/工业读码器 | pyserial |
| network | 网络读码器/PLC 网关 TCP 行推送 | 无 |
| camera | 摄像头识读（条码/QR） | opencv-python、pyzbar |
| sdk | 厂商 SDK/API 挂点（iter_sdk） | 厂商文档 |

新增设备类型=新增一个迭代器适配器，服务端协议不变。厂商专有协议不开放接口时需额外开发或更换设备，不能保证无条件兼容。

## 安全六闸（服务端强制）
1. 设备身份认证（HMAC+时间窗）；2. 条码校验码验真；3. 业务包一次消费查重；
4. 异常预检隔离（超工单/超库存/非正数量）不入正式账簿；5. scan_logs 全留痕；6. 人工复核放行/驳回。

## 标签打印
系统提供条码字符串+明细标签页；图形条码用企业标签软件/打印机驱动按 Code128 生成（常见扫描仪均识读）。