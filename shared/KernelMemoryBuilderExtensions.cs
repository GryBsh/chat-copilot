// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.KernelMemory;

namespace CopilotChat.Shared;

public static class KernelMemoryBuilderExtensions
{
    /// <summary>
    /// Configure the builder using settings from the given KernelMemoryConfig and IConfiguration instances.
    /// </summary>
    /// <param name="builder">KernelMemory builder instance</param>
    /// <param name="memoryConfiguration">KM configuration</param>
    /// <param name="servicesConfiguration">Dependencies configuration, e.g. queue, embedding, storage, etc.</param>
    public static IKernelMemoryBuilder FromMemoryConfiguration(
        this IKernelMemoryBuilder builder,
        KernelMemoryConfig memoryConfiguration,
        IConfiguration servicesConfiguration)
    {
        return new ServiceConfiguration(servicesConfiguration, memoryConfiguration).PrepareBuilder(builder);
    }
}
