﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Core;
using Snap.Hutao.Core.ExceptionService;
using Snap.Hutao.Core.IO.Ini;
using Snap.Hutao.Model.Entity;
using Snap.Hutao.Service.Game.Locator;
using Snap.Hutao.Service.Game.Package;
using Snap.Hutao.View.Dialog;
using Snap.Hutao.Web.Hoyolab.SdkStatic.Hk4e.Launcher;
using Snap.Hutao.Web.Response;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using static Snap.Hutao.Service.Game.GameConstants;

namespace Snap.Hutao.Service.Game;

/// <summary>
/// 游戏服务
/// </summary>
[HighQuality]
[ConstructorGenerated]
[Injection(InjectAs.Singleton, typeof(IGameService))]
internal sealed partial class GameService : IGameService
{
    private readonly PackageConverter packageConverter;
    private readonly IServiceProvider serviceProvider;
    private readonly IGameDbService gameDbService;
    private readonly LaunchOptions launchOptions;
    private readonly RuntimeOptions runtimeOptions;
    private readonly ITaskContext taskContext;
    private readonly AppOptions appOptions;

    private volatile int runningGamesCounter;
    private ObservableCollection<GameAccount>? gameAccounts;

    /// <inheritdoc/>
    public ObservableCollection<GameAccount> GameAccountCollection
    {
        get => gameAccounts ??= gameDbService.GetGameAccountCollection();
    }

    /// <inheritdoc/>
    public async ValueTask<ValueResult<bool, string>> GetGamePathAsync()
    {
        // Cannot find in setting
        if (string.IsNullOrEmpty(appOptions.GamePath))
        {
            IGameLocatorFactory locatorFactory = serviceProvider.GetRequiredService<IGameLocatorFactory>();

            // Try locate by unity log
            ValueResult<bool, string> result = await locatorFactory
                .Create(GameLocationSource.UnityLog)
                .LocateGamePathAsync()
                .ConfigureAwait(false);

            if (!result.IsOk)
            {
                // Try locate by registry
                result = await locatorFactory
                    .Create(GameLocationSource.Registry)
                    .LocateGamePathAsync()
                    .ConfigureAwait(false);
            }

            if (result.IsOk)
            {
                // Save result.
                appOptions.GamePath = result.Value;
            }
            else
            {
                return new(false, SH.ServiceGamePathLocateFailed);
            }
        }

        if (!string.IsNullOrEmpty(appOptions.GamePath))
        {
            return new(true, appOptions.GamePath);
        }
        else
        {
            return new(false, default!);
        }
    }

    /// <inheritdoc/>
    public ChannelOptions GetChannelOptions()
    {
        string gamePath = appOptions.GamePath;
        string configPath = Path.Combine(Path.GetDirectoryName(gamePath) ?? string.Empty, ConfigFileName);
        bool isOversea = string.Equals(Path.GetFileName(gamePath), GenshinImpactFileName, StringComparison.Ordinal);

        if (!File.Exists(configPath))
        {
            return ChannelOptions.FileNotFound(isOversea, configPath);
        }

        using (FileStream stream = File.OpenRead(configPath))
        {
            List<IniParameter> parameters = IniSerializer.Deserialize(stream).OfType<IniParameter>().ToList();
            string? channel = parameters.FirstOrDefault(p => p.Key == ChannelOptions.ChannelName)?.Value;
            string? subChannel = parameters.FirstOrDefault(p => p.Key == ChannelOptions.SubChannelName)?.Value;

            return new(channel, subChannel, isOversea);
        }
    }

    /// <inheritdoc/>
    public bool SetChannelOptions(LaunchScheme scheme)
    {
        string gamePath = appOptions.GamePath;
        string? directory = Path.GetDirectoryName(gamePath);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string configPath = Path.Combine(directory, ConfigFileName);

        List<IniElement> elements = default!;
        try
        {
            using (FileStream readStream = File.OpenRead(configPath))
            {
                elements = IniSerializer.Deserialize(readStream).ToList();
            }
        }
        catch (FileNotFoundException ex)
        {
            ThrowHelper.GameFileOperation(SH.ServiceGameSetMultiChannelConfigFileNotFound.Format(configPath), ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            ThrowHelper.GameFileOperation(SH.ServiceGameSetMultiChannelConfigFileNotFound.Format(configPath), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ThrowHelper.GameFileOperation(SH.ServiceGameSetMultiChannelUnauthorizedAccess, ex);
        }

        bool changed = false;

        foreach (IniElement element in elements)
        {
            if (element is IniParameter parameter)
            {
                if (parameter.Key == "channel")
                {
                    changed = parameter.Set(scheme.Channel.ToString("D"));
                }

                if (parameter.Key == "sub_channel")
                {
                    changed = parameter.Set(scheme.SubChannel.ToString("D"));
                }
            }
        }

        if (changed)
        {
            using (FileStream writeStream = File.Create(configPath))
            {
                IniSerializer.Serialize(writeStream, elements);
            }
        }

        return changed || !LaunchSchemeMatchesExecutable(scheme, Path.GetFileName(gamePath));
    }

    /// <inheritdoc/>
    public async ValueTask<bool> EnsureGameResourceAsync(LaunchScheme launchScheme, IProgress<PackageReplaceStatus> progress)
    {
        string gamePath = appOptions.GamePath;
        string? gameFolder = Path.GetDirectoryName(gamePath);
        ArgumentException.ThrowIfNullOrEmpty(gameFolder);
        string gameFileName = Path.GetFileName(gamePath);

        progress.Report(new(SH.ServiceGameEnsureGameResourceQueryResourceInformation));
        Response<GameResource> response = await serviceProvider
            .GetRequiredService<ResourceClient>()
            .GetResourceAsync(launchScheme)
            .ConfigureAwait(false);

        if (response.IsOk())
        {
            GameResource resource = response.Data;

            if (!LaunchSchemeMatchesExecutable(launchScheme, gameFileName))
            {
                bool replaced = await packageConverter
                    .EnsureGameResourceAsync(launchScheme, resource, gameFolder, progress)
                    .ConfigureAwait(false);

                if (replaced)
                {
                    // We need to change the gamePath if we switched.
                    string exeName = launchScheme.IsOversea ? GenshinImpactFileName : YuanShenFileName;

                    await taskContext.SwitchToMainThreadAsync();
                    appOptions.GamePath = Path.Combine(gameFolder, exeName);
                }
                else
                {
                    // We can't start the game
                    // when we failed to convert game
                    return false;
                }
            }

            if (!launchScheme.IsOversea)
            {
                await packageConverter.EnsureDeprecatedFilesAndSdkAsync(resource, gameFolder).ConfigureAwait(false);
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public bool IsGameRunning()
    {
        if (runningGamesCounter == 0)
        {
            return false;
        }

        if (launchOptions.MultipleInstances)
        {
            // If multiple instances is enabled, always treat as not running.
            return false;
        }

        return Process.GetProcessesByName(YuanShenProcessName).Any()
            || Process.GetProcessesByName(GenshinImpactProcessName).Any();
    }

    /// <inheritdoc/>
    public async ValueTask LaunchAsync()
    {
        if (IsGameRunning())
        {
            return;
        }

        string gamePath = appOptions.GamePath;
        ArgumentException.ThrowIfNullOrEmpty(gamePath);

        using (Process game = ProcessInterop.InitializeGameProcess(launchOptions, gamePath))
        {
            try
            {
                bool isFirstInstance = Interlocked.Increment(ref runningGamesCounter) == 1;

                game.Start();

                bool isAdvancedOptionsAllowed = runtimeOptions.IsElevated && appOptions.IsAdvancedLaunchOptionsEnabled;
                if (isAdvancedOptionsAllowed && launchOptions.MultipleInstances && !isFirstInstance)
                {
                    ProcessInterop.DisableProtection(game, gamePath);
                }

                if (isAdvancedOptionsAllowed && launchOptions.UnlockFps)
                {
                    await ProcessInterop.UnlockFpsAsync(serviceProvider, game, default).ConfigureAwait(false);
                }
                else
                {
                    await game.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref runningGamesCounter);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DetectGameAccountAsync()
    {
        ArgumentNullException.ThrowIfNull(gameAccounts);

        string? registrySdk = RegistryInterop.Get();
        if (!string.IsNullOrEmpty(registrySdk))
        {
            GameAccount? account = null;
            try
            {
                account = gameAccounts.SingleOrDefault(a => a.MihoyoSDK == registrySdk);
            }
            catch (InvalidOperationException ex)
            {
                ThrowHelper.UserdataCorrupted(SH.ServiceGameDetectGameAccountMultiMatched, ex);
            }

            if (account is null)
            {
                // ContentDialog must be created by main thread.
                await taskContext.SwitchToMainThreadAsync();
                LaunchGameAccountNameDialog dialog = serviceProvider.CreateInstance<LaunchGameAccountNameDialog>();
                (bool isOk, string name) = await dialog.GetInputNameAsync().ConfigureAwait(false);

                if (isOk)
                {
                    account = GameAccount.From(name, registrySdk);

                    // sync database
                    await taskContext.SwitchToBackgroundAsync();
                    await gameDbService.AddGameAccountAsync(account).ConfigureAwait(false);

                    // sync cache
                    await taskContext.SwitchToMainThreadAsync();
                    gameAccounts.Add(account);
                }
            }
        }
    }

    /// <inheritdoc/>
    public GameAccount? DetectCurrentGameAccount()
    {
        ArgumentNullException.ThrowIfNull(gameAccounts);

        string? registrySdk = RegistryInterop.Get();

        if (!string.IsNullOrEmpty(registrySdk))
        {
            try
            {
                return gameAccounts.SingleOrDefault(a => a.MihoyoSDK == registrySdk);
            }
            catch (InvalidOperationException ex)
            {
                ThrowHelper.UserdataCorrupted(SH.ServiceGameDetectGameAccountMultiMatched, ex);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public bool SetGameAccount(GameAccount account)
    {
        return RegistryInterop.Set(account);
    }

    /// <inheritdoc/>
    public void AttachGameAccountToUid(GameAccount gameAccount, string uid)
    {
        gameAccount.UpdateAttachUid(uid);
        gameDbService.UpdateGameAccount(gameAccount);
    }

    /// <inheritdoc/>
    public async ValueTask ModifyGameAccountAsync(GameAccount gameAccount)
    {
        await taskContext.SwitchToMainThreadAsync();
        LaunchGameAccountNameDialog dialog = serviceProvider.CreateInstance<LaunchGameAccountNameDialog>();
        (bool isOk, string name) = await dialog.GetInputNameAsync().ConfigureAwait(true);

        if (isOk)
        {
            gameAccount.UpdateName(name);

            // sync database
            await taskContext.SwitchToBackgroundAsync();
            await gameDbService.UpdateGameAccountAsync(gameAccount).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask RemoveGameAccountAsync(GameAccount gameAccount)
    {
        await taskContext.SwitchToMainThreadAsync();
        ArgumentNullException.ThrowIfNull(gameAccounts);
        gameAccounts.Remove(gameAccount);

        await taskContext.SwitchToBackgroundAsync();
        await gameDbService.DeleteGameAccountByIdAsync(gameAccount.InnerId).ConfigureAwait(false);
    }

    private static bool LaunchSchemeMatchesExecutable(LaunchScheme launchScheme, string gameFileName)
    {
        return (launchScheme.IsOversea, gameFileName) switch
        {
            (true, GenshinImpactFileName) => true,
            (false, YuanShenFileName) => true,
            _ => false,
        };
    }
}