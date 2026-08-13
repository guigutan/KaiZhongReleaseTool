# KaiZhongReleaseTool

## 服务器配置数据库

服务器、分组和远程桌面配置保存在程序 EXE 同级的 SQLite 数据库：

`程序目录\servers.db`

复制整个程序目录给其他用户，即可直接复用已配置的服务器列表。复制前请先从系统托盘退出程序，避免数据库仍在写入。

新版首次运行时，如果程序同级没有数据库，会自动从旧位置 `%LOCALAPPDATA%\KaiZhongReleaseTool\servers.db` 迁移现有配置。
