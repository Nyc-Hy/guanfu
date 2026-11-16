namespace Workbench.Web;

/// <summary>
/// 静态资源版本号。
///
/// 浏览器拿到的是本地缓存副本时，改完 CSS / JS 刷新页面看不到变化 ——
/// 人会以为「改了没生效」，其实拿的是旧文件。
/// 解决办法是给资源 URL 带一个版本号：URL 变了，浏览器必然重新拉。
///
/// 开发环境每次启动都换号（改了 CSS 重启一下就能看到）；
/// 生产环境固定，不然每次重启都让所有客户端重拉一遍静态资源。
/// </summary>
public sealed class StaticAssetsVersion
{
    public StaticAssetsVersion(string value) => Value = value;

    public string Value { get; }
}
