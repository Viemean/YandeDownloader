# Yande.re爬虫

需要安装预先安装.net9[下载地址](https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/runtime-desktop-9.0.7-windows-x64-installer)
<br>使用方法：自行配置系统代理，Linux版本chmod+x给予运行权限
<br>启动应用输入要下载的标签，选择过滤级别即可开始下载
<br>不支持断点下载，支持未完全下载标签重启后继续下载，下载文件夹下manifest.json会记录文件大小，图片id和下载时间

###### <br>TODO

* 下载错误项目重试下载
* 校验文件，删除本地已移除文件
