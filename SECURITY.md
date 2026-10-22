# 安全策略

## 支持范围

当前只有 `main` 分支接受安全修复。项目定位是演示与二次开发基础，默认按单实例、内网/本机部署设计。

## 报告问题

请**不要**在公开 Issue 里贴漏洞细节。通过以下任一方式私下联系维护者：

- 邮件：`mercerTang@users.noreply.github.com`
- 或使用 GitHub 的「Report a vulnerability」（Security 标签页）

请附：影响版本、复现步骤、影响面（能否未授权访问 / 能否执行代码 / 能否读写他人数据）、你的修复建议（如果有）。我们会在确认后回复处理计划。

## 部署前必须修改的默认值

仓库里的以下内容都是**开发默认值**，生产部署前必须替换：

| 项 | 位置 | 说明 |
|---|---|---|
| JWT 签名密钥 | `ChatServer/appsettings.json` 的 `Jwt:Key` | 当前是带 `please-change-in-production` 的开发密钥，必须换成随机长密钥 |
| 种子客服 / 管理员密码 | `ChatServer/Services/DatabaseInitializer.cs` | `Agent@123456` / `Admin@123456` 仅供本地演示 |
| CORS | `ChatServer/Program.cs` | 当前允许任意来源，生产应按域名收紧 |
| 文件访问 | `GET /api/files/{id}` | 允许匿名读取，靠 32 位随机名不可枚举；生产应改为签名 URL 或对象存储 |

## 已知设计取舍

- 单实例：在线状态与 SignalR 分组在进程内维护，多实例部署需要 Redis 背板。
- 文件上传：白名单按扩展名 + 图片文件头校验，未做病毒扫描；单文件上限 10 MB。
- 消息保留：默认 30 天，按 `SentAt` 逐条删除消息，保留会话与工单。
