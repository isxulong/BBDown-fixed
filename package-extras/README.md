# package-extras：发行包内的非二进制材料

这里存放 Release 压缩包中除可执行文件外的全部材料，两条路线共用：

- `使用说明-EXE版.txt` / `使用说明-bat版.txt`：两个压缩包各自的使用说明
- `BBDown命令使用指南.txt`：零基础命令教程（bat 版附带）
- `bats/`：bat 版附带的 3 个基础 bat（登录 / 默认下载 / 只看信息不下载），
  仓库内使用简化文件名，压缩包内保留带场景说明的长文件名
- bat 下载命令生成器（HTML 单文件）在 `tools/` 目录

可执行文件（BBDown.exe / BBDownHelper.exe / aria2c.exe / ffmpeg.exe）
不进仓库，只在 Release 里以压缩包形式提供。
