# 贡献指南

感谢你愿意参与这个项目。下面是最短路径，照着做即可。

## 开发环境

- .NET 8 SDK
- Node.js 18+（建议 20 LTS）
- macOS / Linux / Windows 均可；一键脚本 `start.command` 只适用于 macOS

## 本地跑起来

```bash
git clone https://github.com/mercerTang/chatdesk.git
cd chatdesk

# 后端
dotnet build
dotnet test
dotnet run --project ChatServer --urls http://localhost:5180

# 前端（另一个终端）
cd web
npm install
npm run dev        # http://localhost:5173（访客）/ 5174（客服与后台，用 --port 5174 再起一个）
```

演示账号：客服 `agent1` / `agent2` / `agent3`，管理员 `admin`，密码见 README。它们只用于本地开发。

## 提交前必须做

1. `dotnet build` 0 错误。
2. `dotnet test` 全绿（当前 57 个用例）。
3. `cd web && npm run build` 成功。
4. 改了接口、数据模型或行为，同时更新 `docs/` 下对应文档（需求 / 设计 / 批次记录）。

## 分支与提交

- 从 `main` 切分支：`feat/xxx`、`fix/xxx`、`docs/xxx`。
- 提交信息用中文或英文都可以，但要能说清「改了什么、为什么」；一次提交只做一件事。
- 不要在提交里带 `chat.db`、`uploads/`、`node_modules/`、构建产物或本机路径。

## Pull Request

- 描述里写清：背景、改动点、验证方式（命令 + 结果）。
- 涉及界面改动的，附截图或复现步骤。
- 涉及安全边界（鉴权、上传、跨域、文件访问）的，单独说明影响面。

## 代码风格

- 后端跟随现有 C# 风格：文件级 namespace、`record` DTO、中文 XML 注释解释「为什么」。
- 前端跟随现有 Vue 3 `<script setup>` 风格；样式变量统一用 `styles.css` 里的品牌色与灰阶。
- 新增依赖前先说明理由，优先用标准库与现有依赖。
