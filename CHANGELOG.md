# 更新日志

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循语义化版本。

## [Unreleased]

## [0.1.0] - 2026-10-22

首个开源版本：一个可运行的在线客服对话系统，含客服桌面客户端与管理后台。

### 新增

- 访客与客服实时对话：SignalR 广播、服务端权威序号、`ClientMessageId` 幂等、断线补拉、三段回执（已发送 / 已送达 / 已读）
- 会话与工单：一会话一工单、30 天消息保留、工单 TXT / CSV 导出、按客服隔离
- 工单转派：客服转派与管理员强制转派，转派台账记录原客服、目标客服与操作人；服务端同步收回原客服的实时接收权限
- 管理中台：`admin` 角色、客服账号增改停用与重置密码、跨客服会话与工单、强制转派
- 数据统计：概览指标、近 N 天按日趋势（含 0 值日）、按客服维度（会话数 / 本人消息数 / 平均首响 / 当前负载）
- 富文本消息：Quill 2 所见即所得编辑器、Markdown 存储与净化渲染、工具栏悬停提示
- 图片与附件：粘贴 / 拖拽 / 选择上传，≤10 MB，图片内联、附件按原文件名下载
- 客服桌面客户端（Electron）：托盘、系统通知、后端地址可配置、macOS 打包

### 技术栈

- 后端：ASP.NET Core 8、SignalR、EF Core + SQLite、Serilog、PBKDF2 + JWT
- 前端：Vue 3、Vite、Element Plus、vue-router、axios、Quill 2、marked + DOMPurify
- 测试：xUnit + `WebApplicationFactory` + SignalR Client，57 个用例
