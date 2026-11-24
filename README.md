# 玄铁栈道 EquiLedger

企业智能计算治理系统 MVP（按《企业智能计算治理系统项目策划书.md》v1.2 阶段 0–2 + MVP 项落地）。
技术栈：.NET 8 (ASP.NET Core) + PostgreSQL 16 + 原生 Web 前端，全部运行于 Docker。

## 快速开始

```powershell
docker compose up -d --build
```

- Web 界面：http://localhost:8090 （首次使用点击「公司设立向导」，示例账号见向导 JSON）
- PostgreSQL：localhost:5433（迁移账号 equi / 运行账号 equi_app 仅事件表 INSERT/SELECT）
- 工作群推送：设置环境变量 `EQUI_WEBHOOK_URL`（企业微信/钉钉/飞书群机器人 webhook）后 `docker compose up -d`

## 已实现（对应策划书）

- **可信地基（第十章）**：Append-Only 事件存储（应用无 UPDATE/DELETE 权限 + 数据库触发器双保险）、
  SHA-256 哈希链、ECDSA 操作人签名、链头锚定、全量复算校验器（重放重建投影逐表比对）、司法证据包导出、一键验真。
- **治理层（第九章）**：单一授权操作员模式、决议驱动写入 + 决议一致性比对、例行授权额度（单笔/月度/白名单）、
  亲属/利益关联表决回避、无依据不生效、越权尝试告警入链、后备操作员双签移交、工作群自动推送。
- **业务引擎（第五/六/八章）**：股权全生命周期（认缴/实缴/转让/退股/离世冻结）、退股估值推导单、
  分红闭环（利润→公积金→候选稿→决议→20% 代扣→支付）、依赖图自动联动与影响清单、
  薪酬锁定 + 一次性奖金事件 + 离职当日结算 + 社保五险一金 + 个税累计预扣、提成签单时版本锁定、
  复式记账 + 期间锁定 + 红字冲销 + 流水对账异常、合同中心（四级密级/无效条款守卫/覆盖率红牌）、
  模块开关与先决条件守卫（M1–M21）、在线投资合规闭环（合格投资者/合同前置/到账匹配/人数红线/禁保本）。
- **税务规则库 v1（第十一章）**：小微 5%、增值税小规模优惠、六税两费减半、股息 20%、工资累计预扣、
  股权转让 20%、印花税、法定公积金、经营所得 5–35%、个体户减半、合伙先分后税、上市股息差别化；
  全部附政策文号与生效期，内置 2027-12-31 政策悬崖切换。七类企业类型规则包自动匹配。

## 验收测试（A1–A14 核心用例）

```powershell
docker compose up -d db
docker run --rm -v E:\Projects\EquiLedger:/src -v equi_nuget:/root/.nuget/packages `
  -e EQUI_TEST_PG_HOST=host.docker.internal -e EQUI_TEST_PG_PORT=5433 -e EQUI_INIT_SQL=/src/db/init.sql `
  -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test src/EquiLedger.Tests
```

## 目录

```
db/init.sql              数据库结构 + 最小权限 + Append-Only 触发器
src/EquiLedger.Api       后端（Core 密码学 / Storage 事件存储 / Governance 守卫 / Engine 引擎 / Api 端点）
src/EquiLedger.Tests     验收测试（税额确定性 + 治理 + 股权分红 + 薪酬 + 模块投资 + 审计）
web/                     前端（股东只读端 + 操作员录入端 + 一键验真）
```

## 诚实边界（MVP）

- 签名为服务端代签（生产应接 CA 证书/硬件密钥）；锚定为本地记录（生产接可信时间戳/司法存证 API）。
- 政策规则库为 11.3 快照内置，抓取审核管道为后续阶段；工商比对、发票查验接口未接入。
- 本系统辅助治理，不替代法定程序；只做合规计算，不提供逃税方案。