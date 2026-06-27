using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using LogogramHelper.Windows;
using System.Collections.Generic;
using Newtonsoft.Json;
using LogogramHelper.Classes;
using System.Linq;
using System;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace LogogramHelper
{
    public sealed class Plugin : IDalamudPlugin
    {
        private const string SynthesisAddonName = "EurekaMagiciteItemSynthesis";
        private const string ShardListAddonName = "EurekaMagiciteItemShardList";

        public string Name => "Logogram Helper";

        [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] public static IGameGui GameGui { get; private set; } = null!;
        [PluginService] public static IPluginLog Log { get; private set; } = null!;
        [PluginService] public static IDataManager DataManager { get; private set; } = null!;
        [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
        [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

        public WindowSystem WindowSystem = new("LogogramHelper");
        public MainWindow MainWindow { get; init; }
        public LogosWindow LogosWindow { get; init; }

        internal List<LogosAction> LogosActions;
        internal IDictionary<int, Logogram> Logograms;
        internal IDictionary<ulong, LogogramItem> LogogramItems;
        internal IDictionary<int, int> LogogramStock = new Dictionary<int, int>();

        private readonly Queue<int> pendingLogogramSelections = new();
        private int pendingSelectionFrameDelay;

        public Plugin()
        {

            LoadData();

            MainWindow = new MainWindow(this);
            LogosWindow = new LogosWindow(this);

            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(LogosWindow);

            PluginInterface.UiBuilder.Draw += DrawUI;

            AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", ItemDetailOnUpdate);
        }

        public void Dispose()
        {
            AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", ItemDetailOnUpdate);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            this.WindowSystem.RemoveAllWindows();
        }

        private void DrawUI()
        {
            this.WindowSystem.Draw();
            var addonPtr = GameGui.GetAddonByName(SynthesisAddonName, 1);
            if (addonPtr != IntPtr.Zero)
            {
                MainWindow.IsOpen = true;
                ProcessPendingLogogramSelection();
            }
            else
            {
                if (MainWindow.IsOpen) MainWindow.IsOpen = false;
                if (LogosWindow.IsOpen) LogosWindow.IsOpen = false;
                pendingLogogramSelections.Clear();
            }

        }

        private void LoadData()
        {

            using var logogramReader = new StreamReader(Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "logograms.json"));
            var logogramJson = logogramReader.ReadToEnd();
            var Logos = JsonConvert.DeserializeObject<List<Logogram>>(logogramJson);
            Logograms = Logos.ToDictionary(keySelector: l => l.Id, elementSelector: l => l);
            logogramReader.Close();

            using var itemReader = new StreamReader(Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "itemContents.json"));
            var itemJson = itemReader.ReadToEnd();
            var items = JsonConvert.DeserializeObject<List<LogogramItem>>(itemJson);
            LogogramItems = items.ToDictionary(keySelector: i => i.Id, elementSelector: i => i);
            itemReader.Close();

            using var r = new StreamReader(Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "logosActions.json"));
            var logosJson = r.ReadToEnd();
            LogosActions = JsonConvert.DeserializeObject<List<LogosAction>>(logosJson);
            r.Close();

        }

        public void DrawLogosDetailUI(LogosAction action)
        {
            LogosWindow.SetDetails(action);
            LogosWindow.IsOpen = true;
            QueueAutoFill(action);
        }

        private void QueueAutoFill(LogosAction action)
        {
            pendingLogogramSelections.Clear();

            if (GameGui.GetAddonByName(SynthesisAddonName, 1) == IntPtr.Zero)
                return;

            RefreshLogogramStock();

            var recipe = GetCraftableRecipe(action);
            if (recipe == null)
            {
                Log.Information($"No craftable logogram recipe found for action {action.Id}.");
                return;
            }

            foreach (var item in recipe)
            {
                for (var i = 0; i < item.Quantity; i++)
                    pendingLogogramSelections.Enqueue(item.LogogramID);
            }

            pendingSelectionFrameDelay = 0;
        }

        private List<Recipe>? GetCraftableRecipe(LogosAction action)
        {
            foreach (var recipe in action.Recipes)
            {
                var required = recipe
                    .GroupBy(item => item.LogogramID)
                    .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

                if (required.All(item => LogogramStock.TryGetValue(item.Key, out var stock) && stock >= item.Value))
                    return recipe;
            }

            return null;
        }

        private unsafe void ProcessPendingLogogramSelection()
        {
            if (pendingLogogramSelections.Count == 0)
                return;

            if (pendingSelectionFrameDelay > 0)
            {
                pendingSelectionFrameDelay--;
                return;
            }

            var addonPtr = GameGui.GetAddonByName(ShardListAddonName, 1);
            if (addonPtr == IntPtr.Zero)
                return;

            var addon = (AtkUnitBase*)(nint)addonPtr;
            if (!addon->IsReady)
                return;

            var list = GetLogogramList(addon);
            if (list == null)
                return;

            RefreshLogogramStock();

            var logogramId = pendingLogogramSelections.Peek();
            var listIndex = GetLogogramListIndex(logogramId);
            if (listIndex == null)
            {
                Log.Warning($"Could not find logogram {logogramId} in the current shard list.");
                pendingLogogramSelections.Clear();
                return;
            }

            list->ScrollToItem((short)listIndex.Value);
            list->SelectItem(listIndex.Value, false);
            list->DispatchItemEvent(listIndex.Value, AtkEventType.ListItemClick);

            pendingLogogramSelections.Dequeue();
            pendingSelectionFrameDelay = 2;
        }

        private unsafe void RefreshLogogramStock()
        {
            var arrayData = Framework.Instance()->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
            var numberArrayData = arrayData.NumberArrays[(int)NumberArrayType.EurekaLogosShardList];
            if (numberArrayData == null)
                return;

            LogogramStock.Clear();

            for (var i = 1; i <= numberArrayData->IntArray[0]; i++)
            {
                var id = numberArrayData->IntArray[(4 * i) + 1];
                var stock = numberArrayData->IntArray[4 * i];
                LogogramStock[id] = stock;
            }
        }

        private unsafe int? GetLogogramListIndex(int logogramId)
        {
            var arrayData = Framework.Instance()->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
            var numberArrayData = arrayData.NumberArrays[(int)NumberArrayType.EurekaLogosShardList];
            if (numberArrayData == null)
                return null;

            for (var i = 1; i <= numberArrayData->IntArray[0]; i++)
            {
                var id = numberArrayData->IntArray[(4 * i) + 1];
                var stock = numberArrayData->IntArray[4 * i];
                if (id == logogramId && stock > 0)
                    return i - 1;
            }

            return null;
        }

        private static unsafe AtkComponentList* GetLogogramList(AtkUnitBase* addon)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || node->GetNodeType() != NodeType.Component)
                    continue;

                var componentNode = (AtkComponentNode*)node;
                var component = componentNode->Component;
                if (component == null || component->GetComponentType() != ComponentType.List)
                    continue;

                var list = (AtkComponentList*)component;
                if (list->ListLength > 0)
                    return list;
            }

            return null;
        }

        private unsafe void ItemDetailOnUpdate(AddonEvent type, AddonArgs args)
        {
            var id = GameGui.HoveredItem;
            if (LogogramItems.ContainsKey(id))
            {
                var contentsId = LogogramItems[id].Contents;
                var contents = new List<string>();
                contentsId.ForEach(content =>
                {
                    contents.Add(Logograms[content].Name);
                });

                var arrayData = Framework.Instance()->GetUIModule()->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder;
                var stringArrayData = arrayData.StringArrays[27];
                var seStr = GetTooltipString(stringArrayData, 13);
                if (seStr == null) return;

                var insert = $"\n\nPotential logograms contained: {string.Join(", ", contents.ToArray())}";
                if (!seStr.TextValue.Contains(insert)) seStr.Payloads.Insert(1, new TextPayload(insert));

                stringArrayData->SetValue(13, seStr.Encode(), false, true, true);
            }
        }

        private static unsafe SeString? GetTooltipString(StringArrayData* stringArrayData, int field)
        {
            var stringAddress = new IntPtr(stringArrayData->StringArray[field]);
            return stringAddress != IntPtr.Zero ? MemoryHelper.ReadSeStringNullTerminated(stringAddress) : null;
        }
    }
}
