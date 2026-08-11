# LinguaOrb 即时翻译助手

品牌标识“鹿鸣”由梅花鹿、鹿角、梅花斑点与声波组成，表达“鹿之鸣声”。

一个轻巧的 Windows 桌面翻译助手。启动后以悬浮球停靠在桌面右侧中部，点击即可展开翻译卡片。

## 功能

- 中文单词或短语翻译为自然英文
- 英文单词或短语翻译为中文，同时保留英文音标、拼读和发音
- 中文段落自动清洗拆句，输出逐句中英对照与参考语法结构
- 英文段落自动清洗拆句，输出逐句英中对照并分析英文原句语法结构
- 英文口语句先翻译为自然中文，再回译整理为标准英文，并基于标准句分析语法结构
- 展示英文单词、IPA 音标与自然拼读拆分
- 一键播放标准英式发音（优先选择 UK 音频）
- 翻译完成后自动展示与英文结果相关的插图，失败时回退到猫头鹰卡片
- 右侧提供 10 个翻译候选节点，真实测速、点击切换并在失败时自动回退
- 声音页签提供 10 个发音节点，按真实音频下载延迟切换并预加载本地缓存
- 常用示例词支持断网查询
- 无边框、置顶、可拖动的悬浮卡片
- 原创卡通猫头鹰学习伙伴

## 运行

需要 Windows 10/11 与 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet run --project .\LinguaOrb.csproj
```

## 发布单文件程序

```powershell
dotnet publish .\LinguaOrb.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

生成文件位于 `bin\Release\net8.0-windows\win-x64\publish`。

## 数据来源

在线翻译使用 MyMemory 公共接口，音标和发音音频来自 Free Dictionary API，动态配图来自 Wikimedia Commons。公共接口可能有调用频率限制；项目内含少量常用词作为离线兜底。牛津官方音频需要单独获得 Oxford Dictionaries API 授权，本项目不冒充牛津官方数据。

## License

MIT
