using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Agent.T3Code;

public static class T3Services
{
    /// <summary>
    /// T3 Code: the scanner's lookups and the two actions. Whether they do anything is <see cref="T3Options.Enabled"/>,
    /// and the actions also need <c>Agent:Actions:Enabled</c>. Needs <c>AddToolchains</c> (the process runner).
    /// </summary>
    public static IServiceCollection AddT3Code(this IServiceCollection services)
    {
        services.AddSingleton<T3Service>();
        services.AddSingleton<T3Actions>();
        return services;
    }
}
