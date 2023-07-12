﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using CommunityToolkit.WinUI.UI;
using Microsoft.Extensions.Primitives;
using Snap.Hutao.Model.Calculable;
using Snap.Hutao.Model.Entity.Primitive;
using Snap.Hutao.Model.Intrinsic;
using Snap.Hutao.Model.Intrinsic.Immutable;
using Snap.Hutao.Model.Metadata;
using Snap.Hutao.Model.Metadata.Avatar;
using Snap.Hutao.Model.Metadata.Item;
using Snap.Hutao.Model.Primitive;
using Snap.Hutao.Service.Cultivation;
using Snap.Hutao.Service.Hutao;
using Snap.Hutao.Service.Metadata;
using Snap.Hutao.Service.Notification;
using Snap.Hutao.Service.User;
using Snap.Hutao.View.Dialog;
using Snap.Hutao.ViewModel.Complex;
using Snap.Hutao.Web.Response;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CalcAvatarPromotionDelta = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.AvatarPromotionDelta;
using CalcClient = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.CalculateClient;
using CalcConsumption = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.Consumption;
using CalcItem = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.Item;
using CalcItemHelper = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.ItemHelper;

namespace Snap.Hutao.ViewModel.Wiki;

/// <summary>
/// 角色资料视图模型
/// </summary>
[HighQuality]
[ConstructorGenerated]
[Injection(InjectAs.Scoped)]
internal sealed partial class WikiAvatarViewModel : Abstraction.ViewModel
{
    private readonly IServiceProvider serviceProvider;
    private readonly IMetadataService metadataService;
    private readonly ITaskContext taskContext;
    private readonly IHutaoCache hutaoCache;

    private AdvancedCollectionView? avatars;
    private Avatar? selected;
    private string? filterText;
    private BaseValueInfo? baseValueInfo;
    private Dictionary<Level, Dictionary<GrowCurveType, float>>? levelAvatarCurveMap;
    private List<Promote>? promotes;

    /// <summary>
    /// 角色列表
    /// </summary>
    public AdvancedCollectionView? Avatars { get => avatars; set => SetProperty(ref avatars, value); }

    /// <summary>
    /// 选中的角色
    /// </summary>
    public Avatar? Selected
    {
        get => selected; set
        {
            if (SetProperty(ref selected, value))
            {
                UpdateBaseValueInfo(value);
            }
        }
    }

    /// <summary>
    /// 基础数值信息
    /// </summary>
    public BaseValueInfo? BaseValueInfo { get => baseValueInfo; set => SetProperty(ref baseValueInfo, value); }

    /// <summary>
    /// 筛选文本
    /// </summary>
    public string? FilterText { get => filterText; set => SetProperty(ref filterText, value); }

    /// <inheritdoc/>
    protected override async Task OpenUIAsync()
    {
        if (await metadataService.InitializeAsync().ConfigureAwait(false))
        {
            levelAvatarCurveMap = await metadataService.GetLevelToAvatarCurveMapAsync().ConfigureAwait(false);
            promotes = await metadataService.GetAvatarPromotesAsync().ConfigureAwait(false);

            Dictionary<MaterialId, Material> idMaterialMap = await metadataService.GetIdToMaterialMapAsync().ConfigureAwait(false);
            List<Avatar> avatars = await metadataService.GetAvatarsAsync().ConfigureAwait(false);
            List<Avatar> sorted = avatars
                .OrderByDescending(avatar => avatar.BeginTime)
                .ThenByDescending(avatar => avatar.Sort)
                .ToList();

            await CombineComplexDataAsync(sorted, idMaterialMap).ConfigureAwait(false);

            await taskContext.SwitchToMainThreadAsync();
            Avatars = new AdvancedCollectionView(sorted, true);
            Selected = Avatars.Cast<Avatar>().FirstOrDefault();
        }
    }

    private async Task CombineComplexDataAsync(List<Avatar> avatars, Dictionary<MaterialId, Material> idMaterialMap)
    {
        if (await hutaoCache.InitializeForWikiAvatarViewModelAsync().ConfigureAwait(false))
        {
            Dictionary<AvatarId, AvatarCollocationView> idCollocations = hutaoCache.AvatarCollocations!.ToDictionary(a => a.AvatarId);

            foreach (Avatar avatar in avatars)
            {
                avatar.Collocation = idCollocations.GetValueOrDefault(avatar.Id);
                avatar.CookBonusView ??= CookBonusView.Create(avatar.FetterInfo.CookBonus, idMaterialMap);
                avatar.CultivationItemsView ??= avatar.CultivationItems.SelectList(i => idMaterialMap.GetValueOrDefault(i, Material.Default)!);
            }
        }
    }

    [Command("CultivateCommand")]
    private async Task CultivateAsync(Avatar? avatar)
    {
        if (avatar != null)
        {
            IInfoBarService infoBarService = serviceProvider.GetRequiredService<IInfoBarService>();
            IUserService userService = serviceProvider.GetRequiredService<IUserService>();

            if (userService.Current != null)
            {
                // ContentDialog must be created by main thread.
                await taskContext.SwitchToMainThreadAsync();
                CalculableOptions options = new(avatar.ToCalculable(), null);
                CultivatePromotionDeltaDialog dialog = serviceProvider.CreateInstance<CultivatePromotionDeltaDialog>(options);
                (bool isOk, CalcAvatarPromotionDelta delta) = await dialog.GetPromotionDeltaAsync().ConfigureAwait(false);

                if (isOk)
                {
                    Response<CalcConsumption> consumptionResponse = await serviceProvider
                        .GetRequiredService<CalcClient>()
                        .ComputeAsync(userService.Current.Entity, delta)
                        .ConfigureAwait(false);

                    if (consumptionResponse.IsOk())
                    {
                        CalcConsumption consumption = consumptionResponse.Data;

                        List<CalcItem> items = CalcItemHelper.Merge(consumption.AvatarConsume, consumption.AvatarSkillConsume);
                        try
                        {
                            bool saved = await serviceProvider
                                .GetRequiredService<ICultivationService>()
                                .SaveConsumptionAsync(CultivateType.AvatarAndSkill, avatar.Id, items)
                                .ConfigureAwait(false);

                            if (saved)
                            {
                                infoBarService.Success(SH.ViewModelCultivationEntryAddSuccess);
                            }
                            else
                            {
                                infoBarService.Warning(SH.ViewModelCultivationEntryAddWarning);
                            }
                        }
                        catch (Core.ExceptionService.UserdataCorruptedException ex)
                        {
                            infoBarService.Error(ex, SH.ViewModelCultivationAddWarning);
                        }
                    }
                }
            }
            else
            {
                infoBarService.Warning(SH.MustSelectUserAndUid);
            }
        }
    }

    private void UpdateBaseValueInfo(Avatar? avatar)
    {
        if (avatar == null)
        {
            BaseValueInfo = null;
        }
        else
        {
            Dictionary<PromoteLevel, Promote> avatarPromoteMap = promotes!.Where(p => p.Id == avatar.PromoteId).ToDictionary(p => p.Level);
            Dictionary<FightProperty, GrowCurveType> avatarGrowCurve = avatar.GrowCurves.ToDictionary(g => g.Type, g => g.Value);
            FightProperty promoteProperty = avatarPromoteMap[0].AddProperties.Last().Type;

            List<PropertyCurveValue> propertyCurveValues = new()
            {
                new(FightProperty.FIGHT_PROP_BASE_HP, avatarGrowCurve[FightProperty.FIGHT_PROP_BASE_HP], avatar.BaseValue.HpBase),
                new(FightProperty.FIGHT_PROP_BASE_ATTACK, avatarGrowCurve[FightProperty.FIGHT_PROP_BASE_ATTACK], avatar.BaseValue.AttackBase),
                new(FightProperty.FIGHT_PROP_BASE_DEFENSE, avatarGrowCurve[FightProperty.FIGHT_PROP_BASE_DEFENSE], avatar.BaseValue.DefenseBase),
                new(promoteProperty, GrowCurveType.GROW_CURVE_NONE, 0),
            };

            BaseValueInfo = new(avatar.MaxLevel, propertyCurveValues, levelAvatarCurveMap!, avatarPromoteMap);
        }
    }

    [Command("FilterCommand")]
    private void ApplyFilter(string? input)
    {
        if (Avatars != null)
        {
            if (!string.IsNullOrWhiteSpace(input))
            {
                Avatars.Filter = AvatarFilter.Compile(input);

                if (!Avatars.Contains(Selected))
                {
                    try
                    {
                        Avatars.MoveCurrentToFirst();
                    }
                    catch (COMException)
                    {
                    }
                }
            }
            else
            {
                Avatars.Filter = null!;
            }
        }
    }
}