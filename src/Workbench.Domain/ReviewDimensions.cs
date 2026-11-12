namespace Workbench.Domain;

/// <summary>单个维度的评审状态。</summary>
public enum ReviewStatus
{
    /// <summary>还没人给结论。</summary>
    Pending = 0,

    /// <summary>通过。</summary>
    Passed = 1,

    /// <summary>不通过，有问题要改。</summary>
    Failed = 2,

    /// <summary>不适用，跳过。</summary>
    Skipped = 3
}

/// <summary>一轮评审的汇总结论。</summary>
public enum ReviewVerdict
{
    Pending = 0,
    Passed = 1,
    Failed = 2
}

/// <summary>
/// 评审维度的定义（专家团）。
///
/// 维度是固定的七个 —— 它们回答的是不同的问题，不能互相替代：
/// 「代码写得好不好」「注释有没有用」「有没有安全问题」「有没有误伤别的功能」
/// 「需求有没有漏做」「有没有做多」「有没有画蛇添足」。
///
/// 定义放在这里而不是数据库里：维度集合本身是产品的一部分，不该被运行时改。
/// </summary>
public static class ReviewDimensions
{
    public sealed record Definition(
        string Key,
        string Name,
        string Question,
        IReadOnlyList<string> Checklist);

    public static IReadOnlyList<Definition> All { get; } =
    [
        new Definition(
            "code_quality",
            "代码是否合适",
            "这份实现本身写得对不对、选型对不对？",
            [
                "写法是否符合项目既有风格与分层（不要在应用层写 SQL，不要在领域层碰 IO）",
                "数据结构与抽象层次是否恰当，有没有为了未来假设过度设计",
                "错误处理是否完整：失败路径有没有被吞掉、异常类型是否合适",
                "边界条件、空值、并发、幂等是否处理到位",
                "有没有重复逻辑可以复用既有能力",
                "性能是否有明显问题（不必要的 N+1、全表扫、重复解析）"
            ]),

        new Definition(
            "comments",
            "注释是否合适",
            "注释有没有说清「为什么」，而不是复述代码？",
            [
                "注释解释的是设计取舍与约束，而不是把代码念一遍",
                "有没有过时、误导、与当前实现不符的注释",
                "复杂算法、绕过的坑、性能前提是否说明了原因",
                "公开 API 是否有必要的文档说明（参数含义、边界、抛什么异常）",
                "注释语言与项目一致，没有半中半英的混排噪声"
            ]),

        new Definition(
            "security",
            "是否有安全问题",
            "这段改动有没有引入安全问题？",
            [
                "注入风险：SQL、命令、路径、反序列化是否可被外部输入控制",
                "越权：有没有绕过项目/租户/权限边界，能不能访问到不该访问的资源",
                "凭据与敏感信息：硬编码密钥、日志里打印密码/token、个人信息",
                "传输与存储：明文传输、弱哈希、密钥管理是否合规",
                "输入校验与输出编码是否到位（尤其是会被渲染进 HTML 的内容）",
                "依赖是否新增、是否有已知漏洞"
            ]),

        new Definition(
            "scope_impact",
            "是否影响非本次需求的功能",
            "这次改动有没有误伤本次需求之外的东西？",
            [
                "有没有顺手改了与需求无关的文件（格式化、依赖升级、重命名）",
                "公共 API、数据库结构、配置项、协议有没有被意外变更",
                "既有测试有没有被删改来迁就新实现",
                "行为变更有没有影响到调用方（默认值、返回结构、异常类型、时序）",
                "有没有留下调试代码、临时开关、注释掉的代码"
            ]),

        new Definition(
            "missing_requirement",
            "是否有需求未实现",
            "对照需求本身，有没有漏做的地方？",
            [
                "逐条对照计划里的「需求理解」与「验收标准」，有没有没覆盖到的",
                "验收标准的每一条是否都有对应实现与对应测试",
                "异常路径、边界场景有没有被遗漏",
                "文档/接口说明有没有跟着更新",
                "需求里提到的「删除线」内容（已废弃）有没有被误实现"
            ]),

        new Definition(
            "over_implementation",
            "是否有需求过度实现",
            "有没有做了需求根本没要求的事？",
            [
                "有没有实现需求之外的功能、参数、配置项",
                "有没有为「以后可能要用」预留扩展点",
                "有没有引入需求没要求的新依赖或新框架",
                "有没有把简单问题复杂化（多态、插件、策略模式用在一个只有一个实现的地方）"
            ]),

        new Definition(
            "over_operation",
            "是否有过度操作的代码",
            "有没有画蛇添足、没必要的动作？",
            [
                "多余的防御性检查、重复的校验、没有实际作用的重试",
                "无意义的抽象层、薄包装、只被调用一次的 helper",
                "重复或冗余的日志、告警、埋点",
                "不必要的文件/网络/数据库操作（能缓存的没缓存，能批的没批）",
                "为了「显得完整」而写的空实现、占位代码、TODO"
            ])
    ];

    public static Definition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Keys { get; } = All.Select(d => d.Key).ToList();
}
