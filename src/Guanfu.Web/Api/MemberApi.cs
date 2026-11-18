using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Web;

namespace Guanfu.Web.Api;

/// <summary>
/// 成员的登录与成员管理。
///
/// 两条身份线并存，各管各的：
/// - **人工凭证**（数据目录里那把 token）：owner 的万能钥匙，不作废；
/// - **成员会话**（账号 + 密码登录）：团队里每个人的身份，有角色。
///
/// 成员管理这一组动作（建人、改角色、停用、重置密码）改的是**门禁本身**，
/// 所以要「管理员会话」或「owner 的钥匙」才能做。
/// </summary>
public static class MemberApi
{
    public static IEndpointRouteBuilder MapMemberApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (GuanfuException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- 登录 ----

        // 登录本身不需要凭证（这就是「拿到凭证」的那一步）。
        api.MapPost("/auth/login", async (
            LoginRequest request,
            IMemberService members,
            HttpContext context) =>
        {
            var member = await members.AuthenticateAsync(request.UserName, request.Password);

            if (member is null)
            {
                // 不区分「用户名不存在」与「密码不对」—— 多给一点信息就多给攻击者一点信息
                return Results.Json(
                    new { ok = false, error = "用户名或密码不对。" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var entity = await members.GetAsync(member.Id);

            if (entity is null)
            {
                return Results.Json(
                    new { ok = false, error = "用户名或密码不对。" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var principal = MemberAuth.BuildPrincipal(new Member
            {
                Id = entity.Id,
                UserName = entity.UserName,
                DisplayName = entity.DisplayName,
                Role = Enum.Parse<MemberRole>(entity.Role)
            });

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.Add(MemberAuth.SessionLifetime)
                });

            return Results.Ok(new { ok = true, member = entity });
        });

        // 登出不需要凭证 —— 清掉 cookie 就完事了。
        api.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        // 我是谁。界面上显示「现在是谁在操作」靠它。
        api.MapGet("/auth/me", (HttpContext context) =>
        {
            var user = context.User;

            if (user.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new { ok = true, member = (object?)null });
            }

            return Results.Ok(new
            {
                ok = true,
                member = new
                {
                    id = MemberAuth.MemberId(user),
                    userName = user.Identity?.Name,
                    role = user.FindFirst(MemberAuth.RoleClaim)?.Value,
                    isAdmin = MemberAuth.IsAdmin(user)
                }
            });
        });

        // ---- 成员管理（管理员会话或 owner 的钥匙）----

        api.MapGet("/members", async (
            IMemberService members,
            HttpContext context,
            HumanTokenAccessor tokens) =>
        {
            if (!CanManageMembers(context, tokens))
            {
                return Results.Json(
                    new { error = "只有管理员能看成员名单。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(await members.ListAsync());
        });

        api.MapPost("/members", async (
            CreateMemberRequest request,
            IMemberService members,
            HttpContext context,
            HumanTokenAccessor tokens) =>
        {
            if (!CanManageMembers(context, tokens))
            {
                return Results.Json(
                    new { error = "只有管理员能建成员。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var role = Enum.TryParse<MemberRole>(request.Role, ignoreCase: true, out var parsed)
                ? parsed
                : MemberRole.Member;

            var member = await members.CreateAsync(
                request.UserName,
                request.DisplayName,
                request.Password,
                role,
                context.User.Identity?.Name);

            return Results.Created($"/api/members/{member.Id}", member);
        });

        api.MapPut("/members/{memberId:guid}/role", async (
            Guid memberId,
            SetMemberRoleRequest request,
            IMemberService members,
            HttpContext context,
            HumanTokenAccessor tokens) =>
        {
            if (!CanManageMembers(context, tokens))
            {
                return Results.Json(
                    new { error = "只有管理员能改角色。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!Enum.TryParse<MemberRole>(request.Role, ignoreCase: true, out var role))
            {
                return Results.BadRequest(new { error = $"未知角色：{request.Role}" });
            }

            return Results.Ok(await members.SetRoleAsync(memberId, role));
        });

        api.MapPut("/members/{memberId:guid}/status", async (
            Guid memberId,
            SetMemberStatusRequest request,
            IMemberService members,
            HttpContext context,
            HumanTokenAccessor tokens) =>
        {
            if (!CanManageMembers(context, tokens))
            {
                return Results.Json(
                    new { error = "只有管理员能停用成员。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(await members.SetDisabledAsync(memberId, request.Disabled));
        });

        api.MapPost("/members/{memberId:guid}/password", async (
            Guid memberId,
            ResetPasswordRequest request,
            IMemberService members,
            HttpContext context,
            HumanTokenAccessor tokens) =>
        {
            if (!CanManageMembers(context, tokens))
            {
                return Results.Json(
                    new { error = "只有管理员能重置密码。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(await members.ResetPasswordAsync(memberId, request.Password));
        });

        return app;
    }

    /// <summary>
    /// 能不能管成员：**管理员会话**或**owner 的人工凭证**。
    ///
    /// 两者都算「有人在旁边」—— 团队部署时，管理员就是那个「旁边的人」。
    /// </summary>
    private static bool CanManageMembers(HttpContext context, HumanTokenAccessor tokens)
    {
        if (MemberAuth.IsAdmin(context.User))
        {
            return true;
        }

        return HumanTokenPolicy.IsAuthorized(
            context.Request.Headers[HumanTokenPolicy.HeaderName].ToString(),
            context.Request.Cookies[HumanTokenPolicy.CookieName],
            tokens.Token);
    }
}

/// <summary>登录请求。</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>建成员。密码不给 = 这个成员只能靠人工凭证进。</summary>
public sealed record CreateMemberRequest(
    string UserName,
    string? DisplayName,
    string? Password,
    string? Role = null);

/// <summary>改角色。</summary>
public sealed record SetMemberRoleRequest(string Role);

/// <summary>停用 / 启用。</summary>
public sealed record SetMemberStatusRequest(bool Disabled);

/// <summary>重置密码。</summary>
public sealed record ResetPasswordRequest(string Password);
