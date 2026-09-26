# 智慧课堂桥接插件

智慧课堂 App 的配套 ClassIsland 插件：

- 注册「智慧课堂桥接」提醒提供方（召唤通知 / 换课结果 / 手动请求三个渠道）；
- （Slice B）启动 Kestrel localhost 接口（`/notify`、`/exchange`、`/status`），供 App 调用；
- （Slice B）读取档案课表做换课合法性复核，合法时经临时层课表落课。

插件代码保持纯托管、不调用 Windows-only API，使同一 DLL 在 Windows / Linux 版 ClassIsland 上均可加载。
