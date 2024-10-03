using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Remover Tool", "Reneb/Fuji/Arainrr", "4.3.1", ResourceId = 651)]
    [Description("Building and entity removal tool")]
    public class RemoverTool : RustPlugin
    {
        [PluginReference] private readonly Plugin Friends, ServerRewards, Clans, Economics, ImageLibrary;
        private const string PERMISSION_ALL = "removertool.all";
        private const string PERMISSION_ADMIN = "removertool.admin";
        private const string PERMISSION_NORMAL = "removertool.normal";
        private const string PERMISSION_TARGET = "removertool.target";
        private const string PERMISSION_OVERRIDE = "removertool.override";
        private const string PERMISSION_STRUCTURE = "removertool.structure";
        private const string PREFAB_ITEM_DROP = "assets/prefabs/misc/item drop/item_drop.prefab";
        private readonly static int LAYER_PLAYER = LayerMask.GetMask("Player (Server)");
        private readonly static int LAYER_STRUCTURE = LayerMask.GetMask("Construction");
        private readonly static int LAYER_ALL = LayerMask.GetMask("Construction", "Deployed", "Default");

        private static RemoverTool rt;
        private static BUTTON removeButton;
        private bool removeOverride = false;
        private bool removeAllRunning = false;
        private bool removeStructureRunning = false;

        private readonly Dictionary<uint, float> entitySpawnedTimes = new Dictionary<uint, float>();
        private readonly Dictionary<ulong, float> cooldownTimes = new Dictionary<ulong, float>();
        private BuildingGrade.Enum[] BuildingGrades => new BuildingGrade.Enum[] { BuildingGrade.Enum.Twigs, BuildingGrade.Enum.Wood, BuildingGrade.Enum.Stone, BuildingGrade.Enum.Metal, BuildingGrade.Enum.TopTier };

        public enum RemoveType
        {
            All,
            Structure,
            Admin,
            Normal
        }

        #region Oxide Hooks

        private void Init()
        {
            rt = this;
            permission.RegisterPermission(PERMISSION_NORMAL, this);
            permission.RegisterPermission(PERMISSION_ADMIN, this);
            permission.RegisterPermission(PERMISSION_TARGET, this);
            permission.RegisterPermission(PERMISSION_ALL, this);
            permission.RegisterPermission(PERMISSION_STRUCTURE, this);
            permission.RegisterPermission(PERMISSION_OVERRIDE, this);
            cmd.AddChatCommand(configData.chatS.command, this, nameof(CmdRemove));
            foreach (var perm in configData.permissionS.Keys)
                if (!permission.PermissionExists(perm, this))
                    permission.RegisterPermission(perm, this);
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnHammerHit));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnEntityKill));
        }

        private void OnServerInitialized()
        {
            Initialize();
            UpdateConfig();
            if (configData.removerModeS.noHeldMode) removeMode = RemoveMode.NoHeld;
            if (configData.removerModeS.hammerHitMode) removeMode = RemoveMode.HammerHit;
            if (configData.removerModeS.specificTool) removeMode = RemoveMode.SpecificTool;
            if (removeMode == RemoveMode.HammerHit) Subscribe(nameof(OnHammerHit));
            if (configData.raidBlocker.enabled) Subscribe(nameof(OnEntityDeath));
            if (configData.settings.entityTimeLimit)
            {
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnEntityKill));
            }
            if (!Enum.TryParse(configData.settings.removeButton, true, out removeButton))
            {
                PrintError($"{configData.settings.removeButton} is an invalid button. The remover button has been changed to 'FIRE_PRIMARY'.");
                removeButton = BUTTON.FIRE_PRIMARY;
            }
            if (ImageLibrary != null)
            {
                foreach (var image in configData.imageUrls)
                    AddImage(image.Value, image.Key);
            }
        }

        private void Unload()
        {
            foreach (var toolRemover in UnityEngine.Object.FindObjectsOfType<ToolRemover>())
                UnityEngine.Object.Destroy(toolRemover);
            foreach (var player in BasePlayer.activePlayerList)
                DestroyGUI(player);
            rt = null;
        }

        private void OnEntitySpawned(BaseEntity entity)
        {
            if (entity == null || entity.net == null || entity.OwnerID == 0) return;
            if (!IsRemovableEntity(entity)) return;
            if (!entitySpawnedTimes.ContainsKey(entity.net.ID))
                entitySpawnedTimes.Add(entity.net.ID, Time.realtimeSinceStartup);
        }

        private void OnEntityDeath(BuildingBlock buildingBlock, HitInfo info)
        {
            if (buildingBlock == null || info == null) return;
            var attacker = info.InitiatorPlayer;
            if (attacker != null && attacker.userID.IsSteamId() && HasAccess(attacker, buildingBlock)) return;
            BlockRemove(buildingBlock);
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null || entity.net == null || entity.OwnerID == 0) return;
            if (!IsRemovableEntity(entity)) return;
            entitySpawnedTimes.Remove(entity.net.ID);
        }

        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            var toolRemover = player.GetComponent<ToolRemover>();
            if (toolRemover != null)
            {
                toolRemover.hitEntity = info.HitEntity;
                return false;
            }
            return null;
        }

        #endregion Oxide Hooks

        #region Initializing

        private readonly Dictionary<string, string> shorPrefabNameToDeployable = new Dictionary<string, string>();
        private readonly Dictionary<string, string> prefabNameToStructure = new Dictionary<string, string>();
        private readonly Dictionary<string, int> itemShortNameToItemID = new Dictionary<string, int>();
        private readonly HashSet<Construction> constructions = new HashSet<Construction>();

        private void Initialize()
        {
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                if (!itemShortNameToItemID.ContainsKey(itemDefinition.shortname))
                    itemShortNameToItemID.Add(itemDefinition.shortname, itemDefinition.itemid);

                var deployablePrefab = itemDefinition.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                if (string.IsNullOrEmpty(deployablePrefab)) continue;
                var shortPrefabName = GameManager.server.FindPrefab(deployablePrefab)?.GetComponent<BaseEntity>()?.ShortPrefabName;
                if (!shorPrefabNameToDeployable.ContainsKey(shortPrefabName))
                    shorPrefabNameToDeployable.Add(shortPrefabName, itemDefinition.shortname);
            }

            var pooled = GameManifest.Current.pooledStrings.ToDictionary(p => p.str.ToLower(), p => p.hash);
            foreach (var entityPath in GameManifest.Current.entities.ToList())
            {
                var construction = PrefabAttribute.server.Find<Construction>(pooled[entityPath.ToLower()]);
                if (construction != null && construction.deployable == null && !string.IsNullOrEmpty(construction.info.name.english))
                {
                    constructions.Add(construction);
                    if (!prefabNameToStructure.ContainsKey(construction.fullName))
                        prefabNameToStructure.Add(construction.fullName, construction.info.name.english);
                }
            }
        }

        #endregion Initializing

        #region Methods

        private static string GetRemoveTypeName(RemoveType removeType) => rt.configData.removeTypeS[removeType].displayName;

        private static void DropItemContainer(ItemContainer itemContainer, Vector3 dropPosition, Quaternion rotation) => itemContainer?.Drop(PREFAB_ITEM_DROP, dropPosition, rotation);

        public static string GetEntityName(BaseEntity entity)
        {
            if (rt.shorPrefabNameToDeployable.ContainsKey(entity.ShortPrefabName)) return rt.shorPrefabNameToDeployable[entity.ShortPrefabName];
            if (rt.prefabNameToStructure.ContainsKey(entity.PrefabName)) return rt.prefabNameToStructure[entity.PrefabName];
            if (rt.configData.removeInfo.entityS.ContainsKey(entity.ShortPrefabName)) return entity.ShortPrefabName;
            return string.Empty;
        }

        #endregion Methods

        #region UI

        public class UI
        {
            public static CuiElementContainer CreateElementContainer(string parent, string panelName, string backgroundColor, string anchorMin, string anchorMax, bool cursor = false)
            {
                var newElement = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = backgroundColor },
                            RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                            CursorEnabled = cursor
                        },
                        new CuiElement().Parent = parent,
                        panelName
                    }
                };
                return newElement;
            }

            public static void CreatePanel(ref CuiElementContainer container, string panelName, string backgroundColor, string anchorMin, string anchorMax, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = backgroundColor },
                    RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                    CursorEnabled = cursor
                },
                panelName, CuiHelper.GetGuid());
            }

            public static void CreateLabel(ref CuiElementContainer container, string panelName, string textColor, string text, int fontSize, string anchorMin, string anchorMax, TextAnchor align = TextAnchor.MiddleCenter, float fadeIn = 0f)
            {
                container.Add(new CuiLabel
                {
                    Text = { Color = textColor, FontSize = fontSize, Align = align, Text = text, FadeIn = fadeIn },
                    RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
                },
                panelName, CuiHelper.GetGuid());
            }

            public static void CreateImage(ref CuiElementContainer container, string panelName, string image, string anchorMin, string anchorMax)
            {
                CuiRawImageComponent imageComponent = new CuiRawImageComponent { Sprite = "assets/content/textures/generic/fulltransparent.tga" };
                if (image.StartsWith("http") || image.StartsWith("www")) imageComponent.Url = image;
                else imageComponent.Png = image;
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panelName,
                    Components =
                    {
                        imageComponent,
                        new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                    }
                });
            }
        }

        private const string UINAME_MAIN = "RemoverTool";
        private const string UINAME_TIMELIFT = "RemoverToolTimeLeft";
        private const string UINAME_ENTITY = "RemoverToolEntity";
        private const string UINAME_PRICE = "RemoverToolPrice";
        private const string UINAME_REFUND = "RemoverToolRefund";
        private const string UINAME_AUTH = "RemoverToolAuth";

        public static void CreateGUI(BasePlayer player, RemoveType removeType)
        {
            CuiHelper.DestroyUi(player, UINAME_MAIN);
            var container = UI.CreateElementContainer("Hud", UINAME_MAIN, rt.configData.gui.removerToolBackgroundColor, rt.configData.gui.removerToolAnchorMin, rt.configData.gui.removerToolAnchorMax);
            UI.CreatePanel(ref container, UINAME_MAIN, rt.configData.gui.removeBackgroundColor, rt.configData.gui.removeAnchorMin, rt.configData.gui.removeAnchorMax);
            UI.CreateLabel(ref container, UINAME_MAIN, rt.configData.gui.removeTextColor, rt.Lang("RemoverToolType", player.UserIDString, GetRemoveTypeName(removeType)), rt.configData.gui.removeTextSize, rt.configData.gui.removeTextAnchorMin, rt.configData.gui.removeTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.AddUi(player, container);
        }

        public static void TimeLeftUpdate(BasePlayer player, RemoveType removeType, int timeleft, int currentRemoved, int maxRemovable)
        {
            CuiHelper.DestroyUi(player, UINAME_TIMELIFT);
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_TIMELIFT, rt.configData.gui.timeLeftBackgroundColor, rt.configData.gui.timeLeftAnchorMin, rt.configData.gui.timeLeftAnchorMax);
            UI.CreateLabel(ref container, UINAME_TIMELIFT, rt.configData.gui.timeLeftTextColor, rt.Lang("TimeLeft", player.UserIDString, timeleft, removeType == RemoveType.Normal || removeType == RemoveType.Admin ? maxRemovable == 0 ? $"{currentRemoved} / {rt.Lang("Unlimit", player.UserIDString)}" : $"{currentRemoved} / {maxRemovable}" : currentRemoved.ToString()), rt.configData.gui.timeLeftTextSize, rt.configData.gui.timeLeftTextAnchorMin, rt.configData.gui.timeLeftTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.AddUi(player, container);
        }

        public static void EntityUpdate(BasePlayer player, BaseEntity targetEntity)
        {
            CuiHelper.DestroyUi(player, UINAME_ENTITY);
            if (targetEntity == null) return;
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_ENTITY, rt.configData.gui.entityBackgroundColor, rt.configData.gui.entityAnchorMin, rt.configData.gui.entityAnchorMax);
            var name = GetEntityName(targetEntity);
            UI.CreateLabel(ref container, UINAME_ENTITY, rt.configData.gui.entityTextColor, string.IsNullOrEmpty(name) ? targetEntity.ShortPrefabName : GetDisplayName(name), rt.configData.gui.entityTextSize, rt.configData.gui.entityTextAnchorMin, rt.configData.gui.entityTextAnchorMax, TextAnchor.MiddleLeft);
            if (rt.configData.gui.entityImageEnabled && !string.IsNullOrEmpty(name))
            {
                var image = GetEntityImage(name);
                if (!string.IsNullOrEmpty(image))
                    UI.CreateImage(ref container, UINAME_ENTITY, image, rt.configData.gui.entityImageAnchorMin, rt.configData.gui.entityImageAnchorMax);
            }
            CuiHelper.AddUi(player, container);
        }

        public static void PricesUpdate(BasePlayer player, bool usePrice, BaseEntity targetEntity)
        {
            CuiHelper.DestroyUi(player, UINAME_PRICE);
            if (targetEntity == null || !rt.IsRemovableEntity(targetEntity)) return;
            Dictionary<string, int> price = new Dictionary<string, int>();
            if (usePrice) price = rt.GetPrice(targetEntity);
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_PRICE, rt.configData.gui.priceBackgroundColor, rt.configData.gui.priceAnchorMin, rt.configData.gui.priceAnchorMax);
            UI.CreateLabel(ref container, UINAME_PRICE, rt.configData.gui.priceTextColor, rt.Lang("Price", player.UserIDString), rt.configData.gui.priceTextSize, rt.configData.gui.priceTextAnchorMin, rt.configData.gui.priceTextAnchorMax, TextAnchor.MiddleLeft);

            if (price.Count == 0) UI.CreateLabel(ref container, UINAME_PRICE, rt.configData.gui.price2TextColor, rt.Lang("Free", player.UserIDString), rt.configData.gui.price2TextSize, rt.configData.gui.price2TextAnchorMin, rt.configData.gui.price2TextAnchorMax, TextAnchor.MiddleLeft);
            else
            {
                var anchorMin = GetVector2(rt.configData.gui.price2TextAnchorMin);
                var anchorMax = GetVector2(rt.configData.gui.price2TextAnchorMax);
                float x = (anchorMax.y - anchorMin.y) / price.Count;
                int textSize = rt.configData.gui.price2TextSize - price.Count;
                int i = 0;
                foreach (var p in price)
                {
                    UI.CreateLabel(ref container, UINAME_PRICE, rt.configData.gui.price2TextColor, $"{GetDisplayName(p.Key)} x{p.Value}", textSize, $"{anchorMin.x} {anchorMin.y + i * x}", $"{anchorMax.x} {anchorMin.y + (i + 1) * x}", TextAnchor.MiddleLeft);
                    if (rt.configData.gui.imageEnabled)
                    {
                        var image = GetItemImage(p.Key);
                        if (!string.IsNullOrEmpty(image))
                            UI.CreateImage(ref container, UINAME_PRICE, image, $"{anchorMax.x - rt.configData.gui.rightDistance - x * rt.configData.gui.imageScale} {anchorMin.y + i * x}", $"{anchorMax.x - rt.configData.gui.rightDistance} {anchorMin.y + (i + 1) * x}");
                    }
                    i++;
                }
            }
            CuiHelper.AddUi(player, container);
        }

        public static void RefundUpdate(BasePlayer player, bool useRefund, BaseEntity targetEntity)
        {
            CuiHelper.DestroyUi(player, UINAME_REFUND);
            if (targetEntity == null || !rt.IsRemovableEntity(targetEntity)) return;
            Dictionary<string, int> refund = new Dictionary<string, int>();
            if (useRefund) refund = rt.GetRefund(targetEntity);
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_REFUND, rt.configData.gui.refundBackgroundColor, rt.configData.gui.refundAnchorMin, rt.configData.gui.refundAnchorMax);
            UI.CreateLabel(ref container, UINAME_REFUND, rt.configData.gui.refundTextColor, rt.Lang("Refund", player.UserIDString), rt.configData.gui.refundTextSize, rt.configData.gui.refundTextAnchorMin, rt.configData.gui.refundTextAnchorMax, TextAnchor.MiddleLeft);

            if (refund.Count == 0) UI.CreateLabel(ref container, UINAME_REFUND, rt.configData.gui.refund2TextColor, rt.Lang("Nothing", player.UserIDString), rt.configData.gui.refund2TextSize, rt.configData.gui.refund2TextAnchorMin, rt.configData.gui.refund2TextAnchorMax, TextAnchor.MiddleLeft);
            else
            {
                var anchorMin = GetVector2(rt.configData.gui.refund2TextAnchorMin);
                var anchorMax = GetVector2(rt.configData.gui.refund2TextAnchorMax);
                float x = (anchorMax.y - anchorMin.y) / refund.Count;
                int textSize = rt.configData.gui.refund2TextSize - refund.Count;
                int i = 0;
                foreach (var p in refund)
                {
                    UI.CreateLabel(ref container, UINAME_REFUND, rt.configData.gui.refund2TextColor, $"{GetDisplayName(p.Key)} x{p.Value}", textSize, $"{anchorMin.x} {anchorMin.y + i * x}", $"{anchorMax.x} {anchorMin.y + (i + 1) * x}", TextAnchor.MiddleLeft);
                    if (rt.configData.gui.imageEnabled)
                    {
                        var image = GetItemImage(p.Key);
                        if (!string.IsNullOrEmpty(image))
                            UI.CreateImage(ref container, UINAME_REFUND, image, $"{anchorMax.x - rt.configData.gui.rightDistance - x * rt.configData.gui.imageScale} {anchorMin.y + i * x}", $"{anchorMax.x - rt.configData.gui.rightDistance} {anchorMin.y + (i + 1) * x}");
                    }
                    i++;
                }
            }
            CuiHelper.AddUi(player, container);
        }

        public static void AuthorizationUpdate(BasePlayer player, RemoveType removeType, BaseEntity targetEntity, bool shouldPay)
        {
            CuiHelper.DestroyUi(player, UINAME_AUTH);
            if (targetEntity == null) return;
            string reason = string.Empty;
            string color = rt.CanRemoveEntity(player, removeType, targetEntity, shouldPay, ref reason) ? rt.configData.gui.allowedBackgroundColor : rt.configData.gui.refusedBackgroundColor;
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_AUTH, color, rt.configData.gui.authorizationsAnchorMin, rt.configData.gui.authorizationsAnchorMax);
            UI.CreateLabel(ref container, UINAME_AUTH, rt.configData.gui.authorizationsTextColor, reason, rt.configData.gui.authorizationsTextSize, rt.configData.gui.authorizationsTextAnchorMin, rt.configData.gui.authorizationsTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.AddUi(player, container);
        }

        public static void DestroyGUI(BasePlayer player) => CuiHelper.DestroyUi(player, UINAME_MAIN);

        private static string GetEntityImage(string name)
        {
            if (rt.ImageLibrary == null) return string.Empty;
            if (rt.configData.imageUrls.ContainsKey(name))
                return rt.GetImage(name);
            if (rt.itemShortNameToItemID.ContainsKey(name))
                return rt.GetImage(name);
            return string.Empty;
        }

        private static string GetItemImage(string shorname)
        {
            if (rt.ImageLibrary == null) return string.Empty;
            switch (shorname.ToLower())
            {
                case "economics":
                    return rt.GetImage("Economics");

                case "serverrewards":
                    return rt.GetImage("ServerRewards");
            }
            if (rt.configData.imageUrls.ContainsKey(shorname))
                return rt.GetImage(shorname);
            if (rt.itemShortNameToItemID.ContainsKey(shorname))
                return rt.GetImage(shorname);
            return string.Empty;
        }

        private static string GetDisplayName(string name)
        {
            var entityName = rt.shorPrefabNameToDeployable.FirstOrDefault(x => x.Value == name).Key;
            if (string.IsNullOrEmpty(entityName)) entityName = name;
            if (rt.configData.removeInfo.entityS.ContainsKey(entityName))
                return rt.configData.removeInfo.entityS[entityName].displayName;
            if (rt.configData.removeInfo.buildingS.ContainsKey(name))
                return rt.configData.removeInfo.buildingS[name].displayName;
            if (rt.configData.displayNames.ContainsKey(name))
                return rt.configData.displayNames[name];
            else
            {
                var itemDefinition = ItemManager.FindItemDefinition(name);
                if (itemDefinition != null)
                {
                    rt.configData.displayNames.Add(name, itemDefinition.displayName.english);
                    name = itemDefinition.displayName.english;
                }
                else rt.configData.displayNames.Add(name, name);
                rt.SaveConfig();
            }
            return name;
        }

        private string GetImage(string shortname, ulong skin = 0, bool returnUrl = false)
        {
            if (string.IsNullOrEmpty(shortname)) return string.Empty;
            return (string)ImageLibrary?.Call("GetImage", shortname.ToLower(), skin, returnUrl);
        }

        private static Vector2 GetVector2(string anchor) => new Vector2(float.Parse(anchor.Split(' ')[0]), float.Parse(anchor.Split(' ')[1]));

        private bool AddImage(string url, string shortname, ulong skin = 0) => (bool)ImageLibrary?.Call("AddImage", url, shortname.ToLower(), skin);

        #endregion UI

        #region ToolRemover Class

        private static RemoveMode removeMode = RemoveMode.None;

        private enum RemoveMode
        {
            None,
            NoHeld,
            HammerHit,
            SpecificTool
        }

        private class ToolRemover : FacepunchBehaviour
        {
            public BasePlayer player;
            public RemoveType removeType;
            public bool canOverride = false;
            public BaseEntity hitEntity;
            public int currentRemoved = 0;

            private int timeLeft;
            private float distance;
            private float lastRemove = 0f;
            private float removeInterval = 0.2f;
            private bool pay = false;
            private bool refund = false;
            private int maxRemovable = 0;
            private RaycastHit raycastHit;
            private BaseEntity targetEntity;
            private uint currentItemID;
            private Item heldItem;
            private int removeTime;
            private bool resetTime;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                if (removeMode == RemoveMode.NoHeld)
                {
                    currentItemID = player.svActiveItemID;
                    UnEquip();
                }
                if (removeMode == RemoveMode.SpecificTool)
                    heldItem = player.GetActiveItem();
            }

            public void Init(RemoveType type, int time, int max, float dis, float interval, bool p, bool r, bool reset, bool c)
            {
                canOverride = c;
                removeTime = timeLeft = time;
                removeType = type;
                distance = dis;
                resetTime = reset;
                removeInterval = interval;
                if (removeInterval < 0.2f) removeInterval = 0.2f;
                if (removeType == RemoveType.Normal)
                {
                    maxRemovable = max;
                    pay = p && rt.configData.removeInfo.priceEnabled;
                    refund = r && rt.configData.removeInfo.refundEnabled;
                }
                else
                {
                    maxRemovable = currentRemoved = 0;
                    pay = refund = false;
                }
                CreateGUI(player, removeType);
                CancelInvoke(RemoveUpdate);
                InvokeRepeating(RemoveUpdate, 0f, 1f);
            }

            private void RemoveUpdate()
            {
                TimeLeftUpdate(player, removeType, timeLeft, currentRemoved, maxRemovable);
                GetTargetEntity();
                EntityUpdate(player, targetEntity);
                if (removeType == RemoveType.Normal)
                {
                    if (rt.configData.gui.authorizationEnabled)
                        AuthorizationUpdate(player, removeType, targetEntity, pay);
                    if (rt.configData.gui.priceEnabled)
                        PricesUpdate(player, pay, targetEntity);
                    if (rt.configData.gui.refundEnabled)
                        RefundUpdate(player, refund, targetEntity);
                }
                if (timeLeft-- <= 0)
                    Destroy(this);
            }

            private void GetTargetEntity()
            {
                bool flag = Physics.Raycast(player.eyes.HeadRay(), out raycastHit, distance, Rust.Layers.Solid);
                targetEntity = flag ? raycastHit.GetEntity() : null;
            }

            private bool IsSpecificTool()
            {
                heldItem = player.GetActiveItem();
                if (heldItem != null && heldItem.info.shortname == rt.configData.removerModeS.shortname)
                {
                    if (rt.configData.removerModeS.skin < 0) return true;
                    return heldItem.skin == (ulong)rt.configData.removerModeS.skin;
                }
                return false;
            }

            private void FixedUpdate()
            {
                if (player.IsSleeping() || !player.IsConnected)
                {
                    Destroy(this);
                    return;
                }
                if (removeMode == RemoveMode.NoHeld && player.svActiveItemID != currentItemID)
                {
                    currentItemID = player.svActiveItemID;
                    if (currentItemID != 0)
                    {
                        if (rt.configData.removerModeS.disableInHand)
                        {
                            Destroy(this);
                            return;
                        }
                        UnEquip();
                    }
                }
                if (Time.realtimeSinceStartup - lastRemove >= removeInterval)
                {
                    if (removeMode != RemoveMode.HammerHit)
                    {
                        if (!player.serverInput.IsDown(removeButton)) return;
                        if (removeMode == RemoveMode.SpecificTool && !IsSpecificTool()) return;
                        GetTargetEntity();
                        if (rt.TryRemove(player, targetEntity, removeType, distance, pay, refund))
                        {
                            if (resetTime) timeLeft = removeTime;
                            if (removeType == RemoveType.Normal || removeType == RemoveType.Admin)
                                currentRemoved++;
                        }
                    }
                    else
                    {
                        if (hitEntity == null) return;
                        targetEntity = hitEntity;
                        if (rt.TryRemove(player, targetEntity, removeType, distance, pay, refund))
                        {
                            if (resetTime) timeLeft = removeTime;
                            if (removeType == RemoveType.Normal || removeType == RemoveType.Admin)
                                currentRemoved++;
                        }
                        hitEntity = null;
                    }
                    lastRemove = Time.realtimeSinceStartup;
                }
                if (removeType == RemoveType.Normal && maxRemovable > 0 && currentRemoved >= maxRemovable)
                {
                    rt.Print(player, rt.Lang("EntityLimit", player.UserIDString, maxRemovable));
                    Destroy(this);
                };
            }

            private void UnEquip()
            {
                var item = player.GetActiveItem();
                if (item == null) return;
                var heldEntity = item.GetHeldEntity() as HeldEntity;
                if (heldEntity == null) return;
                var slot = item.position;
                item.SetParent(null);
                rt.timer.Once(0.15f, () =>
                {
                    if (item == null) return;
                    item.position = slot;
                    item.SetParent(player.inventory.containerBelt);
                });
            }

            private void OnDestroy()
            {
                CancelInvoke(RemoveUpdate);
                DestroyGUI(player);
                if (removeType == RemoveType.Normal && rt != null)
                    rt.cooldownTimes[player.userID] = Time.realtimeSinceStartup;
            }
        }

        #endregion ToolRemover Class

        #region Pay

        private bool Pay(BasePlayer player, BaseEntity targetEntity)
        {
            var cost = GetPrice(targetEntity);
            try
            {
                List<Item> collect = new List<Item>();
                foreach (var p in cost)
                {
                    if (p.Value <= 0) continue;
                    if (itemShortNameToItemID.ContainsKey(p.Key))
                    {
                        var itemid = itemShortNameToItemID[p.Key];
                        player.inventory.Take(collect, itemid, p.Value);
                        player.Command("note.inv", itemid, -p.Value);
                    }
                    else if (!CheckOrPayCoins(p.Key, p.Value, player.userID)) return false;
                }
                foreach (Item item in collect) item.Remove();
            }
            catch (Exception e)
            {
                PrintError($"{player} couldn't pay to remove entity. Error Message: {e.Message}");
                return false;
            }
            return true;
        }

        private Dictionary<string, int> GetPrice(BaseEntity targetEntity)
        {
            var buildingblock = targetEntity.GetComponent<BuildingBlock>();
            if (buildingblock != null)
            {
                var entityName = prefabNameToStructure[buildingblock.PrefabName];
                if (configData.removeInfo.buildingS.ContainsKey(entityName))
                {
                    var grades = configData.removeInfo.buildingS[entityName];
                    if (grades.buildingGrade.ContainsKey(buildingblock.grade))
                    {
                        float p = 0;
                        if (float.TryParse(grades.buildingGrade[buildingblock.grade].price.ToString(), out p))
                        {
                            var currentGrade = buildingblock.currentGrade;
                            if (currentGrade != null)
                            {
                                var price = new Dictionary<string, int>();
                                foreach (var itemAmount in currentGrade.costToBuild)
                                {
                                    var amount = (int)(itemAmount.amount * p / 100);
                                    if (amount < 1) continue;
                                    price.Add(itemAmount.itemDef.shortname, amount);
                                }
                                return price;
                            }
                        }
                        else
                        {
                            if (grades.buildingGrade[buildingblock.grade].price is Dictionary<string, int>)
                                return grades.buildingGrade[buildingblock.grade].price as Dictionary<string, int>;
                            try
                            {
                                return JsonConvert.DeserializeObject<Dictionary<string, int>>(grades.buildingGrade[buildingblock.grade].price.ToString());
                            }
                            catch (Exception e)
                            {
                                PrintError($"Wrong price format for '{buildingblock.grade}' of '{entityName}' in 'Building Blocks Settings'. Error Message: {e.Message}");
                                var currentGrade = buildingblock.currentGrade;
                                if (currentGrade != null)
                                    return currentGrade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)y.amount);
                            }
                        }
                    }
                }
            }
            else if (configData.removeInfo.entityS.ContainsKey(targetEntity.ShortPrefabName))
                return configData.removeInfo.entityS[targetEntity.ShortPrefabName].price;
            return new Dictionary<string, int>();
        }

        private bool CanPay(BasePlayer player, BaseEntity targetEntity)
        {
            var cost = GetPrice(targetEntity);
            if (cost.Count == 0) return true;
            foreach (var p in cost)
            {
                if (p.Value <= 0) continue;
                if (itemShortNameToItemID.ContainsKey(p.Key))
                {
                    int c = player.inventory.GetAmount(itemShortNameToItemID[p.Key]);
                    if (c < p.Value) return false;
                }
                else if (!CheckOrPayCoins(p.Key, p.Value, player.userID, true)) return false;
            }
            return true;
        }

        private bool CheckOrPayCoins(string key, int price, ulong playerID, bool check = false)
        {
            if (price <= 0) return true;
            switch (key.ToLower())
            {
                case "economics":
                    if (Economics == null) return false;
                    if (check)
                    {
                        var b = Economics.CallHook("Balance", playerID);
                        if (b == null) return false;
                        if ((double)b < price) return false;
                    }
                    else
                    {
                        var w = Economics.CallHook("Withdraw", playerID, (double)price);
                        if (w == null || !(bool)w) return false;
                    }
                    return true;

                case "serverrewards":
                    if (ServerRewards == null) return false;
                    if (check)
                    {
                        var c = ServerRewards.CallHook("CheckPoints", playerID);
                        if (c == null) return false;
                        if ((int)c < price) return false;
                    }
                    else
                    {
                        var t = ServerRewards.CallHook("TakePoints", playerID, price);
                        if (t == null || !(bool)t) return false;
                    }
                    return true;
            }
            return true;
        }

        #endregion Pay

        #region Refund

        private void GiveRefund(BasePlayer player, BaseEntity targetEntity)
        {
            var refund = GetRefund(targetEntity);
            foreach (var r in refund)
            {
                if (r.Value <= 0) continue;
                if (itemShortNameToItemID.ContainsKey(r.Key))
                {
                    var item = ItemManager.CreateByItemID(itemShortNameToItemID[r.Key], r.Value);
                    player.GiveItem(item);
                }
                else
                {
                    switch (r.Key.ToLower())
                    {
                        case "economics":
                            if (Economics == null) continue;
                            Economics.CallHook("Deposit", player.userID, (double)r.Value);
                            continue;

                        case "serverrewards":
                            if (ServerRewards == null) continue;
                            ServerRewards.CallHook("AddPoints", player.userID, r.Value);
                            continue;
                        default:
                            PrintError($"{player} didn't receive refund because {r.Key} doesn't seem to be a valid item name");
                            continue;
                    }
                }
            }
        }

        private Dictionary<string, int> GetRefund(BaseEntity targetEntity)
        {
            var refund = new Dictionary<string, int>();
            var buildingblock = targetEntity.GetComponent<BuildingBlock>();
            if (buildingblock != null)
            {
                var entityName = prefabNameToStructure[buildingblock.PrefabName];
                if (configData.removeInfo.buildingS.ContainsKey(entityName))
                {
                    var grades = configData.removeInfo.buildingS[entityName];
                    if (grades.buildingGrade.ContainsKey(buildingblock.grade))
                    {
                        float r = 0;
                        if (float.TryParse(grades.buildingGrade[buildingblock.grade].refund.ToString(), out r))
                        {
                            var currentGrade = buildingblock.currentGrade;
                            if (currentGrade != null)
                            {
                                foreach (var itemAmount in currentGrade.costToBuild)
                                {
                                    var amount = (int)(itemAmount.amount * r / 100);
                                    if (amount < 1) continue;
                                    refund.Add(itemAmount.itemDef.shortname, amount);
                                }
                            }
                        }
                        else
                        {
                            if (grades.buildingGrade[buildingblock.grade].refund is Dictionary<string, int>)
                                return grades.buildingGrade[buildingblock.grade].refund as Dictionary<string, int>;
                            try
                            {
                                return JsonConvert.DeserializeObject<Dictionary<string, int>>(grades.buildingGrade[buildingblock.grade].refund.ToString());
                            }
                            catch (Exception e)
                            {
                                PrintError($"Wrong refund format for '{buildingblock.grade}' of '{entityName}' in 'Building Blocks Settings'. Error Message: {e.Message}");
                                var currentGrade = buildingblock.currentGrade;
                                if (currentGrade != null)
                                    return currentGrade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)y.amount);
                            }
                        }
                    }
                }
            }
            else
            {
                if (configData.removeInfo.entityS.ContainsKey(targetEntity.ShortPrefabName))
                    refund = configData.removeInfo.entityS[targetEntity.ShortPrefabName].refund;
                if (configData.removeInfo.refundSlot)
                {
                    var slots = GetSlot(targetEntity);
                    if (slots.Count > 0)
                    {
                        var r = new Dictionary<string, int>();
                        var union = refund.Keys.Union(slots.Keys);
                        int value;
                        foreach (var u in union)
                        {
                            value = 0;
                            if (refund.ContainsKey(u) && slots.ContainsKey(u))
                                value = refund[u] + slots[u];
                            else
                            {
                                if (refund.ContainsKey(u)) value = refund[u];
                                if (slots.ContainsKey(u)) value = slots[u];
                            }
                            r.Add(u, value);
                        }
                        return r;
                    }
                }
            }
            return refund;
        }

        private Dictionary<string, int> GetSlot(BaseEntity targetEntity)
        {
            var slots = new Dictionary<string, int>();
            foreach (int value in Enum.GetValues(typeof(BaseEntity.Slot)))
            {
                var slot = (BaseEntity.Slot)value;
                if (targetEntity.HasSlot(slot))
                {
                    var s = targetEntity.GetSlot(slot);
                    if (s != null)
                    {
                        if (rt.shorPrefabNameToDeployable.ContainsKey(s.ShortPrefabName))
                        {
                            var name = rt.shorPrefabNameToDeployable[s.ShortPrefabName];
                            slots.Add(name, 1);
                        }
                    }
                }
            }
            return slots;
        }

        #endregion Refund

        #region RaidBlocker

        private readonly Hash<uint, float> lastAttackedBuildings = new Hash<uint, float>();
        private readonly Hash<ulong, float> lastBlockedPlayers = new Hash<ulong, float>();

        private void BlockRemove(BuildingBlock buildingBlock)
        {
            if (configData.raidBlocker.blockBuildingID)
            {
                var buildingID = buildingBlock.buildingID;
                lastAttackedBuildings[buildingID] = Time.realtimeSinceStartup;
            }
            if (configData.raidBlocker.blockPlayers)
            {
                foreach (var collider in Physics.OverlapSphere(buildingBlock.transform.position, configData.raidBlocker.blockRadius, LAYER_PLAYER))
                {
                    var player = collider.GetComponent<BasePlayer>();
                    if (player.userID.IsSteamId()) lastBlockedPlayers[player.userID] = Time.realtimeSinceStartup;
                }
            }
        }

        private bool IsRaidBlocked(BasePlayer player, BaseEntity targetEntity, ref float timeLeft)
        {
            if (configData.raidBlocker.blockBuildingID)
            {
                var buildingBlock = targetEntity.GetComponent<BuildingBlock>();
                if (buildingBlock != null)
                {
                    if (configData.raidBlocker.blockTime > (Time.realtimeSinceStartup - lastAttackedBuildings[buildingBlock.buildingID]))
                        return true;
                }
            }
            if (configData.raidBlocker.blockPlayers)
            {
                if (configData.raidBlocker.blockTime > (Time.realtimeSinceStartup - lastBlockedPlayers[player.userID]))
                    return true;
            }
            return false;
        }

        #endregion RaidBlocker

        #region TryRemove

        private bool TryRemove(BasePlayer player, BaseEntity targetEntity, RemoveType removeType, float distance, bool shouldPay, bool shouldRefund)
        {
            if (targetEntity == null)
            {
                Print(player, Lang("NotFoundOrFar", player.UserIDString));
                return false;
            }
            string reason = string.Empty;
            if (!CanRemoveEntity(player, removeType, targetEntity, shouldPay, ref reason))
            {
                Print(player, reason);
                return false;
            }
            if (removeType == RemoveType.Admin)
            {
                DoRemove(targetEntity, configData.removeTypeS[RemoveType.Admin].gibs);
                return true;
            }
            if (removeType == RemoveType.All)
            {
                if (removeAllRunning)
                {
                    Print(player, Lang("AlreadyRemoveAll", player.UserIDString));
                    return false;
                }
                ServerMgr.Instance.StartCoroutine(RemoveAll(targetEntity, player));
                Print(player, Lang("StartRemoveAll", player.UserIDString));
                return true;
            }
            if (removeType == RemoveType.Structure)
            {
                if (removeStructureRunning)
                {
                    Print(player, Lang("AlreadyRemoveStructure", player.UserIDString));
                    return false;
                }
                if (RemoveStructure(targetEntity, player))
                {
                    Print(player, Lang("StartRemoveStructure", player.UserIDString));
                    return true;
                }
                Print(player, Lang("NotStructure", player.UserIDString));
                return false;
            }

            if (targetEntity is StorageContainer)
            {
                var storageContainer = targetEntity as StorageContainer;
                if (storageContainer.inventory?.itemList?.Count > 0)
                {
                    if (configData.storageS.dropContainerStorage)
                        DropItemContainer(storageContainer.inventory, storageContainer.GetDropPosition(), storageContainer.transform.rotation);
                    else if (configData.storageS.dropItmesStorage)
                        DropUtil.DropItems(storageContainer.inventory, storageContainer.transform.position);
                }
            }
            else if (targetEntity is ContainerIOEntity)
            {
                var containerIOEntity = targetEntity as ContainerIOEntity;
                if (containerIOEntity.inventory?.itemList?.Count > 0)
                {
                    if (configData.storageS.dropContainerIOEntity)
                        DropItemContainer(containerIOEntity.inventory, containerIOEntity.GetDropPosition(), containerIOEntity.transform.rotation);
                    else if (configData.storageS.dropItmesIOEntity)
                        DropUtil.DropItems(containerIOEntity.inventory, containerIOEntity.transform.position);
                }
            }
            if (shouldPay)
            {
                bool flag = Pay(player, targetEntity);
                if (!flag)
                {
                    Print(player, Lang("CantPay", player.UserIDString));
                    return false;
                }
            }
            if (shouldRefund) GiveRefund(player, targetEntity);
            DoRemove(targetEntity, configData.removeTypeS[RemoveType.Normal].gibs);
            return true;
        }

        private bool CanRemoveEntity(BasePlayer player, RemoveType removeType, BaseEntity targetEntity, bool shouldPay, ref string reason)
        {
            if (removeType == RemoveType.Admin) return true;
            if (!IsRemovableEntity(targetEntity) || targetEntity.IsDestroyed)
            {
                reason = Lang("InvalidEntity", player.UserIDString);
                return false;
            }
            if (removeType != RemoveType.Normal) return true;
            if (!IsValidEntity(targetEntity))
            {
                reason = Lang("EntityDisabled", player.UserIDString);
                return false;
            }
            var obj = Interface.CallHook("canRemove", player);
            if (obj != null)
            {
                reason = obj is string ? (string)obj : Lang("BeBlocked", player.UserIDString);
                return false;
            }
            if (!configData.fractioned.enabled && IsDamagedEntity(targetEntity))
            {
                reason = Lang("DamagedEntity", player.UserIDString);
                return false;
            }
            float timeLeft = 0f;
            if (configData.raidBlocker.enabled && IsRaidBlocked(player, targetEntity, ref timeLeft))
            {
                reason = Lang("RaidBlocked", player.UserIDString, Math.Ceiling(timeLeft));
                return false;
            }
            if (configData.settings.entityTimeLimit && IsEntityTimeLimit(targetEntity))
            {
                reason = Lang("EntityTimeLimit", player.UserIDString, configData.settings.limitTime);
                return false;
            }
            if (shouldPay && !CanPay(player, targetEntity))
            {
                reason = Lang("NotEnoughCost", player.UserIDString);
                return false;
            }
            if (!configData.storageS.removeNotEmptyStorage && targetEntity is StorageContainer)
            {
                if ((targetEntity as StorageContainer).inventory?.itemList?.Count > 0)
                {
                    reason = Lang("StorageNotEmpty", player.UserIDString);
                    return false;
                }
            }
            if (!configData.storageS.removeNotEmptyIOEntity && targetEntity is ContainerIOEntity)
            {
                if ((targetEntity as ContainerIOEntity).inventory?.itemList?.Count > 0)
                {
                    reason = Lang("StorageNotEmpty", player.UserIDString);
                    return false;
                }
            }
            if (HasAccess(player, targetEntity))
            {
                if (configData.settings.checkStash && HasStash(targetEntity as BuildingBlock))//Prevent not access players from knowing that there is stash
                {
                    reason = Lang("HasStash", player.UserIDString);
                    return false;
                }
                reason = Lang("CanRemove", player.UserIDString);
                return true;
            }
            reason = Lang("NotRemoveAccess", player.UserIDString);
            return false;
        }

        private bool HasAccess(BasePlayer player, BaseEntity targetEntity)
        {
            if (configData.settings.useEntityOwners)
            {
                if (targetEntity.OwnerID == player.userID || AreFriends(targetEntity.OwnerID.ToString(), player.UserIDString))
                {
                    if (!configData.settings.useToolCupboards) return true;
                    else if (HasTotalAccess(player, targetEntity)) return true;
                }
            }
            if (configData.settings.useBuildingOwners)
            {
                BuildingBlock buildingRef = targetEntity.GetComponent<BuildingBlock>();
                if (buildingRef == null)
                {
                    RaycastHit supportHit;
                    if (Physics.Raycast(targetEntity.transform.position + new Vector3(0f, 0.1f, 0f), new Vector3(0f, -1f, 0f), out supportHit, 3f, LAYER_STRUCTURE))
                        buildingRef = supportHit.GetEntity() as BuildingBlock;
                }
                if (buildingRef != null)
                {
                    var returnhook = Interface.CallHook("FindBlockData", buildingRef);
                    if (returnhook != null && returnhook is string)
                    {
                        string ownerID = (string)returnhook;
                        if (player.UserIDString == ownerID) return true;
                        if (AreFriends(ownerID, player.UserIDString)) return true;
                    }
                }
            }
            if (configData.settings.useToolCupboards && HasTotalAccess(player, targetEntity))
            {
                if (configData.settings.useEntityOwners)
                {
                    if (targetEntity.OwnerID == player.userID || AreFriends(targetEntity.OwnerID.ToString(), player.UserIDString))
                        return true;
                    return false;
                }
                return true;
            }
            return false;
        }

        private bool HasTotalAccess(BasePlayer player, BaseEntity targetEntity)
        {
            if (player.IsBuildingBlocked(targetEntity.WorldSpaceBounds()))
                return false;
            return true;
        }

        private bool AreFriends(string playerID, string friendID)
        {
            if (configData.settings.useFriends && Friends != null)
            {
                var r = Friends.CallHook("HasFriend", playerID, friendID);
                if (r != null && (bool)r) return true;
            }
            if (configData.settings.useClans && Clans != null)
            {
                if (Clans.ResourceId == 842)//Rust:IO Clans
                {
                    var playerClan = Clans.Call("GetClanOf", playerID);
                    var friendClan = Clans.Call("GetClanOf", friendID);
                    if (playerClan != null && friendClan != null)
                        return (string)playerClan == (string)friendClan;
                }
                else//Clans
                {
                    var isMember = Clans.Call("IsClanMember", playerID, friendID);
                    if (isMember != null && (bool)isMember) return true;
                }
            }
            return false;
        }

        private bool IsRemovableEntity(BaseEntity entity) => !string.IsNullOrEmpty(GetEntityName(entity));

        private bool IsValidEntity(BaseEntity entity)
        {
            var buildingBlock = entity.GetComponent<BuildingBlock>();
            if (buildingBlock != null && configData.removeInfo.validConstruction.ContainsKey(buildingBlock.grade) && !configData.removeInfo.validConstruction[buildingBlock.grade]) return false;
            if (configData.removeInfo.entityS.ContainsKey(entity.ShortPrefabName) && !configData.removeInfo.entityS[entity.ShortPrefabName].enabled) return false;
            return true;
        }

        private bool HasStash(BuildingBlock buildingBlock)
        {
            if (buildingBlock == null) return false;
            if (buildingBlock.ShortPrefabName.Contains("foundation"))
            {
                var position = new OBB(buildingBlock.transform, buildingBlock.bounds).ToBounds().center;
                var stashes = Pool.GetList<StashContainer>();
                Vis.Entities(position, 2.5f, stashes);
                if (stashes.Count > 0)
                {
                    Pool.FreeList(ref stashes);
                    return true;
                }
                Pool.FreeList(ref stashes);
            }
            return false;
        }

        private bool IsDamagedEntity(BaseEntity entity)
        {
            if (configData.fractioned.excludeBuildingBlocks && (entity is BuildingBlock || entity is SimpleBuildingBlock)) return false;
            var baseCombatEntity = entity as BaseCombatEntity;
            if (baseCombatEntity == null || !baseCombatEntity.repair.enabled) return false;
            if (!(entity is BuildingBlock) && (baseCombatEntity.repair.itemTarget == null || baseCombatEntity.repair.itemTarget.Blueprint == null))
                return false;
            if ((baseCombatEntity.Health() / baseCombatEntity.MaxHealth() * 100f) >= configData.fractioned.percentage) return false;
            return true;
        }

        private bool IsEntityTimeLimit(BaseEntity entity)
        {
            if (entity.net == null) return true;
            if (entitySpawnedTimes.ContainsKey(entity.net.ID))
                return (Time.realtimeSinceStartup - entitySpawnedTimes[entity.net.ID]) > configData.settings.limitTime;
            return true;
        }

        #endregion TryRemove

        #region Remove Entity

        private IEnumerator RemoveAll(BaseEntity sourceEntity, BasePlayer player)
        {
            removeAllRunning = true;
            var current = 0;
            var checkFrom = new List<Vector3> { sourceEntity.transform.position };
            var removeList = new List<BaseEntity>();

            while (true)
            {
                if (current >= checkFrom.Count) break;
                var list = Pool.GetList<BaseEntity>();
                Vis.Entities(checkFrom[current], 3f, list, LAYER_ALL);
                for (int i = 0; i < list.Count; i++)
                {
                    var entity = list[i];
                    if (removeList.Contains(entity)) continue;
                    removeList.Add(entity);
                    if (!checkFrom.Contains(entity.transform.position)) checkFrom.Add(entity.transform.position);
                }
                Pool.FreeList(ref list);
                if (current++ % configData.settings.removePerFrame == 0) yield return new WaitForEndOfFrame();
            }
            if (configData.settings.notItemContainerDrop)
            {
                var list1 = new List<BaseEntity>();
                foreach (var entity in removeList)
                    if (entity is StorageContainer || entity is ContainerIOEntity) list1.Add(entity);
                foreach (var entity in removeList)
                    if (!(entity is StorageContainer) && !(entity is ContainerIOEntity)) list1.Add(entity);
                removeList = list1;
            }
            else
            {
                foreach (var entity in removeList)
                {
                    if (entity is StorageContainer)
                        DropItemContainer((entity as StorageContainer).inventory, entity.GetDropPosition(), entity.transform.rotation);
                    if (entity is ContainerIOEntity)
                        DropItemContainer((entity as ContainerIOEntity).inventory, entity.GetDropPosition(), entity.transform.rotation);
                }
            }
            ServerMgr.Instance.StartCoroutine(DelayRemove(removeList, player, configData.removeTypeS[RemoveType.All].gibs, true));
            yield break;
        }

        private bool RemoveStructure(BaseEntity sourceEntity, BasePlayer player)
        {
            var buildingBlock = sourceEntity.GetComponent<BuildingBlock>();
            if (buildingBlock == null) return false;
            removeStructureRunning = true;
            var removeList = new List<BaseEntity>();
            var building = buildingBlock.GetBuilding();
            if (building != null)
            {
                if (configData.settings.notItemContainerDrop)
                {
                    foreach (var decayEntity in building.decayEntities)
                        if (decayEntity is StorageContainer) removeList.Add(decayEntity);
                    foreach (var decayEntity in building.decayEntities)
                        if (!(decayEntity is StorageContainer)) removeList.Add(decayEntity);
                }
                else
                {
                    foreach (var decayEntity in building.decayEntities)
                    {
                        removeList.Add(decayEntity);
                        if (decayEntity is StorageContainer)
                            DropItemContainer((decayEntity as StorageContainer).inventory, decayEntity.GetDropPosition(), decayEntity.transform.rotation);
                    }
                }
            }
            else removeList.Add(sourceEntity);
            ServerMgr.Instance.StartCoroutine(DelayRemove(removeList, player, configData.removeTypeS[RemoveType.Structure].gibs));
            return true;
        }

        public IEnumerator DelayRemove(List<BaseEntity> entities, BasePlayer player, bool gibs = true, bool all = false)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                DoRemove(entities[i], gibs);
                if (i % configData.settings.removePerFrame == 0) yield return new WaitForEndOfFrame();
            }
            var toolRemover = player?.GetComponent<ToolRemover>();
            if (all)
            {
                removeAllRunning = false;
                if (toolRemover != null && toolRemover.removeType == RemoveType.All) toolRemover.currentRemoved += entities.Count;
                if (player != null) Print(player, Lang("CompletedRemoveAll", player.UserIDString, entities.Count));
            }
            else
            {
                removeStructureRunning = false;
                if (toolRemover != null && toolRemover.removeType == RemoveType.Structure) toolRemover.currentRemoved += entities.Count;
                if (player != null) Print(player, Lang("CompletedRemoveStructure", player.UserIDString, entities.Count));
            }
            yield break;
        }

        private void DoRemove(BaseEntity entity, bool gibs = true)
        {
            if (entity != null && !entity.IsDestroyed)
            {
                Interface.CallHook("OnRemovedEntity", entity);
                entity.Kill(gibs ? BaseNetworkable.DestroyMode.Gib : BaseNetworkable.DestroyMode.None);
            }
        }

        #endregion Remove Entity

        #region Commands

        private void CmdRemove(BasePlayer player, string command, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                var sourceRemover = player.GetComponent<ToolRemover>();
                if (sourceRemover != null)
                {
                    UnityEngine.Object.Destroy(sourceRemover);
                    Print(player, Lang("ToolDisabled", player.UserIDString));
                    return;
                }
            }
            if (removeOverride && !permission.UserHasPermission(player.UserIDString, PERMISSION_OVERRIDE))
            {
                Print(player, Lang("CurrentlyDisabled", player.UserIDString));
                return;
            }
            RemoveType removeType = RemoveType.Normal;
            int time = configData.removeTypeS[removeType].defaultTime;
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "n":
                    case "normal":
                        break;

                    case "a":
                    case "admin":
                        removeType = RemoveType.Admin;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_ADMIN));
                            return;
                        }
                        break;

                    case "all":
                        removeType = RemoveType.All;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ALL))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_ALL));
                            return;
                        }
                        break;

                    case "s":
                    case "structure":
                        removeType = RemoveType.Structure;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_STRUCTURE))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_STRUCTURE));
                            return;
                        }
                        break;

                    case "h":
                    case "help":
                        StringBuilder stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine(Lang("Syntax", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Normal)));
                        stringBuilder.AppendLine(Lang("Syntax1", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Admin)));
                        stringBuilder.AppendLine(Lang("Syntax2", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.All)));
                        stringBuilder.AppendLine(Lang("Syntax3", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Structure)));
                        Print(player, stringBuilder.ToString());
                        return;

                    default:
                        if (int.TryParse(args[0], out time)) break;
                        Print(player, Lang("SyntaxError", player.UserIDString, configData.chatS.command));
                        return;
                }
            }
            ConfigData.PermissionS permissionS = new ConfigData.PermissionS();
            if (removeType == RemoveType.Normal)
            {
                if (!permission.UserHasPermission(player.UserIDString, PERMISSION_NORMAL))
                {
                    Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_NORMAL));
                    return;
                }
                permissionS = GetPermissionS(player);
            }
            if (args.Length > 1) int.TryParse(args[1], out time);
            ToggleRemove(player, removeType, time, permissionS);
        }

        private bool ToggleRemove(BasePlayer player, RemoveType removeType, int time, ConfigData.PermissionS permissionS)
        {
            int maxRemovable = 0;
            float distance = configData.removeTypeS[removeType].distance;
            bool pay = false, refund = false;
            int maxTime = configData.removeTypeS[removeType].maxTime;
            bool resetTime = configData.removeTypeS[removeType].resetTime;
            float interval = configData.settings.removeInterval;
            if (removeType == RemoveType.Normal)
            {
                var cooldown = permissionS.cooldown;
                if (cooldown > 0 && !(configData.settings.cooldownExclude && player.IsAdmin))
                {
                    if (cooldownTimes.ContainsKey(player.userID))
                    {
                        var tick = Time.realtimeSinceStartup - cooldownTimes[player.userID];
                        if (tick < cooldown)
                        {
                            Print(player, Lang("Cooldown", player.UserIDString, Math.Ceiling(cooldown - tick)));
                            return false;
                        }
                    }
                }
                interval = permissionS.removeInterval;
                resetTime = permissionS.resetTime;
                maxTime = permissionS.maxTime;
                maxRemovable = permissionS.maxRemovable;
                if (configData.settings.maxRemovableExclude && player.IsAdmin) maxRemovable = 0;
                distance = permissionS.distance;
                pay = permissionS.pay;
                refund = permissionS.refund;
            }
            if (time > maxTime) time = maxTime;
            var removerTool = player.GetComponent<ToolRemover>();
            if (removerTool == null) removerTool = player.gameObject.AddComponent<ToolRemover>();
            else if (removerTool.removeType == RemoveType.Normal)
                cooldownTimes[player.userID] = Time.realtimeSinceStartup;
            removerTool.Init(removeType, time, maxRemovable, distance, interval, pay, refund, resetTime, true);
            Print(player, Lang("ToolEnabled", player.UserIDString, time, maxRemovable == 0 ? Lang("Unlimit", player.UserIDString) : maxRemovable.ToString(), GetRemoveTypeName(removeType)));
            return true;
        }

        private ConfigData.PermissionS GetPermissionS(BasePlayer player)
        {
            int priority = 0;
            ConfigData.PermissionS permissionS = new ConfigData.PermissionS();
            foreach (var entry in configData.permissionS)
            {
                if (permission.UserHasPermission(player.UserIDString, entry.Key) && entry.Value.priority >= priority)
                {
                    priority = entry.Value.priority;
                    permissionS = entry.Value;
                }
            }
            return permissionS;
        }

        [ConsoleCommand("remove.toggle")]
        private void CCmdRemoveToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Print(arg, "Syntax error!!!Please enter the commands in the game F1 console");
                return;
            }
            CmdRemove(player, string.Empty, arg.Args);
        }

        [ConsoleCommand("remove.target")]
        private void CCmdRemoveTarget(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (arg?.Args == null || arg.Args.Length <= 1)
            {
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("Syntax error of target command");
                stringBuilder.AppendLine("remove.target <disable | d> <player (name or id)> - Disable remover tool for player");
                stringBuilder.AppendLine("remove.target <normal | n> <player (name or id)> [time (seconds)] [max removable objects (integer)] - Enable remover tool for player (Normal)");
                stringBuilder.AppendLine("remove.target <admin | a> <player (name or id)> [time (seconds)] - Enable remover tool for player (Admin)");
                stringBuilder.AppendLine("remove.target <all> <player (name or id)> [time (seconds)] - Enable remover tool for player (All)");
                stringBuilder.AppendLine("remove.target <structure | s> <player (name or id)> [time (seconds)] - Enable remover tool for player (Structure)");
                Print(arg, stringBuilder.ToString());
                return;
            }
            if (player != null && !permission.UserHasPermission(player.UserIDString, PERMISSION_TARGET))
            {
                Print(arg, Lang("NotAllowed", player.UserIDString, PERMISSION_TARGET));
                return;
            }
            var target = RustCore.FindPlayer(arg.Args[1]);
            if (target == null || !target.IsConnected)
            {
                Print(arg, Lang("PlayerNotFound", player?.UserIDString, arg.Args[0]));
                return;
            }
            RemoveType removeType = RemoveType.Normal;
            switch (arg.Args[0].ToLower())
            {
                case "n":
                case "normal":
                    removeType = RemoveType.Normal;
                    break;

                case "a":
                case "admin":
                    removeType = RemoveType.Admin;
                    break;

                case "all":
                    removeType = RemoveType.All;
                    break;

                case "s":
                case "structure":
                    removeType = RemoveType.Structure;
                    break;

                case "d":
                case "disable":
                    var toolRemover = target.GetComponent<ToolRemover>();
                    if (toolRemover != null)
                    {
                        UnityEngine.Object.Destroy(toolRemover);
                        Print(arg, $"{target}'s remover tool is disabled");
                    }
                    else Print(arg, $"{target} did not enable the remover tool");
                    return;

                default:
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.AppendLine("Syntax error of target command");
                    stringBuilder.AppendLine("remove.target <disable | d> <player (name or id)> - Disable remover tool for player");
                    stringBuilder.AppendLine("remove.target <normal | n> <player (name or id)> [time (seconds)] [max removable objects (integer)] - Enable remover tool for player (Normal)");
                    stringBuilder.AppendLine("remove.target <admin | a> <player (name or id)> [time (seconds)] - Enable remover tool for player (Admin)");
                    stringBuilder.AppendLine("remove.target <all> <player (name or id)> [time (seconds)] - Enable remover tool for player (All)");
                    stringBuilder.AppendLine("remove.target <structure | s> <player (name or id)> [time (seconds)] - Enable remover tool for player (Structure)");
                    Print(arg, stringBuilder.ToString());
                    return;
            }
            int maxRemovable = 0;
            int time = configData.removeTypeS[removeType].defaultTime;
            if (arg.Args.Length > 2) int.TryParse(arg.Args[2], out time);
            if (arg.Args.Length > 3 && removeType == RemoveType.Normal) int.TryParse(arg.Args[3], out maxRemovable);
            var targetRemover = target.GetComponent<ToolRemover>();
            if (targetRemover == null) targetRemover = target.gameObject.AddComponent<ToolRemover>();
            targetRemover.Init(removeType, time, maxRemovable, configData.removeTypeS[removeType].distance, configData.permissionS[PERMISSION_NORMAL].removeInterval, configData.permissionS[PERMISSION_NORMAL].pay, configData.permissionS[PERMISSION_NORMAL].refund, configData.permissionS[PERMISSION_NORMAL].resetTime, false);
            Print(arg, Lang("TargetEnabled", player?.UserIDString, target, time, maxRemovable, GetRemoveTypeName(removeType)));
        }

        [ConsoleCommand("remove.building")]
        private void CCmdConstruction(ConsoleSystem.Arg arg)
        {
            if (arg?.Args == null || arg.Args.Length <= 1 || !arg.IsAdmin)
            {
                Print(arg, $"Syntax error, Please enter 'remove.building <price / refund / priceP / refundP> <percentage>'");
                return;
            }
            switch (arg.Args[0].ToLower())
            {
                case "price":
                    float p = 50f;
                    float.TryParse(arg.Args[1], out p);
                    foreach (var construction in constructions)
                    {
                        if (configData.removeInfo.buildingS.ContainsKey(construction.info.name.english))
                        {
                            var buildingGrade = configData.removeInfo.buildingS[construction.info.name.english].buildingGrade;
                            foreach (var entry in buildingGrade)
                            {
                                var grade = construction.grades[(int)entry.Key];
                                entry.Value.price = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)(y.amount * p / 100));
                            }
                        }
                    }
                    Print(arg, $"Successfully modified all building prices to {p}% of the initial cost.");
                    SaveConfig();
                    return;

                case "refund":
                    float r = 40f;
                    float.TryParse(arg.Args[1], out r);
                    foreach (var construction in constructions)
                    {
                        if (configData.removeInfo.buildingS.ContainsKey(construction.info.name.english))
                        {
                            var buildingGrade = configData.removeInfo.buildingS[construction.info.name.english].buildingGrade;
                            foreach (var entry in buildingGrade)
                            {
                                var grade = construction.grades[(int)entry.Key];
                                entry.Value.refund = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)(y.amount * r / 100));
                            }
                        }
                    }
                    Print(arg, $"Successfully modified all building refunds to {r}% of the initial cost.");
                    SaveConfig();
                    return;

                case "pricep":
                    float pp = 40f;
                    float.TryParse(arg.Args[1], out pp);
                    foreach (var value in configData.removeInfo.buildingS.Values)
                        foreach (var data in value.buildingGrade.Values)
                            data.price = pp;
                    Print(arg, $"Successfully modified all building prices to {pp}% of the initial cost.");
                    SaveConfig();
                    return;

                case "refundp":
                    float rr = 50f;
                    float.TryParse(arg.Args[1], out rr);
                    foreach (var value in configData.removeInfo.buildingS.Values)
                        foreach (var data in value.buildingGrade.Values)
                            data.refund = rr;
                    Print(arg, $"Successfully modified all building refunds to {rr}% of the initial cost.");
                    SaveConfig();
                    return;

                default:
                    Print(arg, $"Syntax error, Please enter 'remove.building <price / refund / priceP / refundP> <percentage>'");
                    return;
            }
        }

        [ConsoleCommand("remove.allow")]
        private void CCmdRemoveAllow(ConsoleSystem.Arg arg)
        {
            if (arg?.Args == null || arg.Args.Length == 0)
            {
                Print(arg, "Syntax error, Please enter 'remove.allow <true | false>'");
                return;
            }
            var player = arg?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PERMISSION_OVERRIDE))
            {
                Print(arg, Lang("NotAllowed", player.UserIDString, PERMISSION_OVERRIDE));
                return;
            }
            switch (arg.Args[0].ToLower())
            {
                case "true":
                case "1":
                    removeOverride = false;
                    Print(arg, "Remove is now allowed depending on your settings.");
                    return;

                case "false":
                case "0":
                    removeOverride = true;
                    Print(arg, "Remove is now restricted for all players (exept admins)");
                    foreach (var toolremover in UnityEngine.Object.FindObjectsOfType<ToolRemover>())
                    {
                        if (toolremover.removeType == RemoveType.Normal && toolremover.canOverride)
                        {
                            Print(toolremover.player, "The remover tool has been disabled by the admin");
                            UnityEngine.Object.Destroy(toolremover);
                        }
                    }
                    return;

                default:
                    Print(arg, "This is not a valid argument");
                    return;
            }
        }

        #endregion Commands

        #region ConfigurationFile

        private void UpdateConfig()
        {
            foreach (var @enum in BuildingGrades)
                if (!configData.removeInfo.validConstruction.ContainsKey(@enum))
                    configData.removeInfo.validConstruction.Add(@enum, true);

            foreach (var construction in constructions)
            {
                if (!configData.removeInfo.buildingS.ContainsKey(construction.info.name.english))
                {
                    var buildingGrade = new Dictionary<BuildingGrade.Enum, ConfigData.RemoveInfo.BBData.RData>();
                    foreach (var @enum in BuildingGrades)
                    {
                        var grade = construction.grades[(int)@enum];
                        buildingGrade.Add(@enum, new ConfigData.RemoveInfo.BBData.RData { refund = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)(y.amount * 0.4)), price = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => (int)(y.amount * 0.6)) });
                    }
                    configData.removeInfo.buildingS.Add(construction.info.name.english, new ConfigData.RemoveInfo.BBData { displayName = construction.info.name.english, buildingGrade = buildingGrade });
                }
            }
            foreach (var entry in shorPrefabNameToDeployable)
                if (!configData.removeInfo.entityS.ContainsKey(entry.Key))
                    configData.removeInfo.entityS.Add(entry.Key, new ConfigData.RemoveInfo.EntityS { enabled = true, displayName = ItemManager.FindItemDefinition(entry.Value)?.displayName?.english, refund = new Dictionary<string, int> { [entry.Value] = 1 }, price = new Dictionary<string, int>() });

            SaveConfig();
        }

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Settings")]
            public Settings settings = new Settings();

            public class Settings
            {
                [JsonProperty(PropertyName = "Use Clans")]
                public bool useClans = true;

                [JsonProperty(PropertyName = "Use Friends")]
                public bool useFriends = true;

                [JsonProperty(PropertyName = "Use Entity Owners")]
                public bool useEntityOwners = true;

                [JsonProperty(PropertyName = "Use Tool Cupboards (Strongly unrecommended)")]
                public bool useToolCupboards = false;

                [JsonProperty(PropertyName = "Use Building Owners (You will need BuildingOwners plugin)")]
                public bool useBuildingOwners = false;

                [JsonProperty(PropertyName = "Remove Button")]
                public string removeButton = BUTTON.FIRE_PRIMARY.ToString();

                [JsonProperty(PropertyName = "Remove Interval (Min = 0.2)")]
                public float removeInterval = 0.5f;

                [JsonProperty(PropertyName = "RemoveType - All/Structure - Remove per frame")]
                public int removePerFrame = 20;

                [JsonProperty(PropertyName = "RemoveType - All/Structure - No item container dropped")]
                public bool notItemContainerDrop = true;

                [JsonProperty(PropertyName = "RemoveType - Normal - Max Removable Objects - Exclude admins")]
                public bool maxRemovableExclude = true;

                [JsonProperty(PropertyName = "RemoveType - Normal - Cooldown - Exclude admins")]
                public bool cooldownExclude = true;

                [JsonProperty(PropertyName = "RemoveType - Normal - Check stash under the foundation")]
                public bool checkStash = false;

                [JsonProperty(PropertyName = "RemoveType - Normal - Entity Spawned Time Limit - Enabled")]
                public bool entityTimeLimit = false;

                [JsonProperty(PropertyName = "RemoveType - Normal - Entity Spawned Time Limit - Cannot be removed when entity spawned time more than it")]
                public float limitTime = 300f;
            }

            [JsonProperty(PropertyName = "Container Settings")]
            public StorageS storageS = new StorageS();

            public class StorageS
            {
                [JsonProperty(PropertyName = "Storage Container - Enable remove of not empty storages")]
                public bool removeNotEmptyStorage = true;

                [JsonProperty(PropertyName = "Storage Container - Drop items from container")]
                public bool dropItmesStorage = false;

                [JsonProperty(PropertyName = "Storage Container - Drop a item container from container")]
                public bool dropContainerStorage = true;

                [JsonProperty(PropertyName = "IOEntity Container - Enable remove of not empty storages")]
                public bool removeNotEmptyIOEntity = true;

                [JsonProperty(PropertyName = "IOEntity Container - Drop items from container")]
                public bool dropItmesIOEntity = false;

                [JsonProperty(PropertyName = "IOEntity Container - Drop a item container from container")]
                public bool dropContainerIOEntity = true;
            }

            [JsonProperty(PropertyName = "Remove Damaged Entities")]
            public Fractioned fractioned = new Fractioned();

            public class Fractioned
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool enabled = false;

                [JsonProperty(PropertyName = "Exclude Building Blocks")]
                public bool excludeBuildingBlocks = true;

                [JsonProperty(PropertyName = "Percentage (Can be removed when (health / max health * 100) is not less than it)")]
                public float percentage = 90f;
            }

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings chatS = new ChatSettings();

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Chat Command")]
                public string command = "remove";

                [JsonProperty(PropertyName = "Chat Prefix")]
                public string prefix = "[RemoverTool]: ";

                [JsonProperty(PropertyName = "Chat Prefix Color")]
                public string prefixColor = "#00FFFF";

                [JsonProperty(PropertyName = "Chat SteamID Icon")]
                public ulong steamIDIcon = 0;
            }

            [JsonProperty(PropertyName = "Permission Settings (Just for normal type)")]
            public Dictionary<string, PermissionS> permissionS = new Dictionary<string, PermissionS>()
            {
                [PERMISSION_NORMAL] = new PermissionS { priority = 0, distance = 3, cooldown = 60, maxTime = 300, maxRemovable = 50, removeInterval = 1f, pay = true, refund = true, resetTime = false },
                ["removertool.vip"] = new PermissionS { priority = 1, distance = 5, cooldown = 30, maxTime = 300, maxRemovable = 100, removeInterval = 0.5f, pay = false, refund = true, resetTime = true }
            };

            public class PermissionS
            {
                [JsonProperty(PropertyName = "Priority")]
                public int priority;

                [JsonProperty(PropertyName = "Distance")]
                public float distance;

                [JsonProperty(PropertyName = "Cooldown")]
                public float cooldown;

                [JsonProperty(PropertyName = "Max Time")]
                public int maxTime;

                [JsonProperty(PropertyName = "Remove Interval (Min = 0.2)")]
                public float removeInterval = 0.5f;

                [JsonProperty(PropertyName = "Max Removable Objects (0 = Unlimit)")]
                public int maxRemovable;

                [JsonProperty(PropertyName = "Pay")]
                public bool pay;

                [JsonProperty(PropertyName = "Refund")]
                public bool refund;

                [JsonProperty(PropertyName = "Reset the time after removing a entity")]
                public bool resetTime = false;
            }

            [JsonProperty(PropertyName = "Remove Type Settings")]
            public Dictionary<RemoveType, RemoveTypeS> removeTypeS = new Dictionary<RemoveType, RemoveTypeS>()
            {
                [RemoveType.Normal] = new RemoveTypeS { displayName = RemoveType.Normal.ToString(), distance = 3, gibs = true, defaultTime = 60, maxTime = 300, resetTime = false },
                [RemoveType.Structure] = new RemoveTypeS { displayName = RemoveType.Structure.ToString(), distance = 100, gibs = false, defaultTime = 300, maxTime = 600, resetTime = true },
                [RemoveType.All] = new RemoveTypeS { displayName = RemoveType.All.ToString(), distance = 100, gibs = false, defaultTime = 300, maxTime = 600, resetTime = true },
                [RemoveType.Admin] = new RemoveTypeS { displayName = RemoveType.Admin.ToString(), distance = 20, gibs = true, defaultTime = 300, maxTime = 600, resetTime = true },
            };

            public class RemoveTypeS
            {
                [JsonProperty(PropertyName = "Display Name")]
                public string displayName;

                [JsonProperty(PropertyName = "Distance")]
                public float distance;

                [JsonProperty(PropertyName = "Default Time")]
                public int defaultTime;

                [JsonProperty(PropertyName = "Max Time")]
                public int maxTime;

                [JsonProperty(PropertyName = "Gibs")]
                public bool gibs;

                [JsonProperty(PropertyName = "Reset the time after removing a entity")]
                public bool resetTime = false;
            }

            [JsonProperty(PropertyName = "Remove Mode Settings (Only one model works)")]
            public RemoverModeS removerModeS = new RemoverModeS();

            public class RemoverModeS
            {
                [JsonProperty(PropertyName = "No Held Item Mode")]
                public bool noHeldMode = true;

                [JsonProperty(PropertyName = "No Held Item Mode - Disable remover tool when you have any item in hand")]
                public bool disableInHand = true;

                [JsonProperty(PropertyName = "Hammer Hit Mode")]
                public bool hammerHitMode = false;

                [JsonProperty(PropertyName = "Specific Tool Mode")]
                public bool specificTool = false;

                [JsonProperty(PropertyName = "Specific Tool Mode - Item shortname")]
                public string shortname = "hammer";

                [JsonProperty(PropertyName = "Specific Tool Mode - Item skin (-1 = All skins)")]
                public long skin = -1;
            }

            [JsonProperty(PropertyName = "Raid Blocker Settings")]
            public RaidBlocker raidBlocker = new RaidBlocker();

            public class RaidBlocker
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool enabled = false;

                [JsonProperty(PropertyName = "Block Time")]
                public float blockTime = 300;

                [JsonProperty(PropertyName = "By Buildings")]
                public bool blockBuildingID = true;

                [JsonProperty(PropertyName = "By Surrounding Players")]
                public bool blockPlayers = true;

                [JsonProperty(PropertyName = "By Surrounding Players - Radius")]
                public float blockRadius = 120;
            }

            [JsonProperty(PropertyName = "Image Urls (Used to UI image)")]
            public Dictionary<string, string> imageUrls = new Dictionary<string, string>()
            {
                ["Economics"] = "https://s2.ax1x.com/2020/02/04/10vsnP.png",
                ["ServerRewards"] = "https://s2.ax1x.com/2020/02/02/1YeCO1.png",
            };

            [JsonProperty(PropertyName = "Display Names Of Other Things")]
            public Dictionary<string, string> displayNames = new Dictionary<string, string>();

            [JsonProperty(PropertyName = "GUI")]
            public GUI gui = new GUI();

            public class GUI
            {
                [JsonProperty(PropertyName = "Main Box - Min Anchor (in Rust Window)")]
                public string removerToolAnchorMin = "0.1 0.55";

                [JsonProperty(PropertyName = "Main Box - Max Anchor (in Rust Window)")]
                public string removerToolAnchorMax = "0.4 0.95";

                [JsonProperty(PropertyName = "Main Box - Background Color")]
                public string removerToolBackgroundColor = "0 0 0 0";

                [JsonProperty(PropertyName = "Remove Title - Box - Min Anchor (in Main Box)")]
                public string removeAnchorMin = "0 0.85";

                [JsonProperty(PropertyName = "Remove Title - Box - Max Anchor (in Main Box)")]
                public string removeAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Remove Title - Box - Background Color")]
                public string removeBackgroundColor = "0 1 1 0.9";

                [JsonProperty(PropertyName = "Remove Title - Text - Min Anchor (in Main Box)")]
                public string removeTextAnchorMin = "0.05 0.85";

                [JsonProperty(PropertyName = "Remove Title - Text - Max Anchor (in Main Box)")]
                public string removeTextAnchorMax = "0.6 1";

                [JsonProperty(PropertyName = "Remove Title - Text - Text Color")]
                public string removeTextColor = "1 0 0 0.9";

                [JsonProperty(PropertyName = "Remove Title - Text - Text Size")]
                public int removeTextSize = 18;

                [JsonProperty(PropertyName = "Timeleft - Box - Min Anchor (in Main Box)")]
                public string timeLeftAnchorMin = "0.6 0.85";

                [JsonProperty(PropertyName = "Timeleft - Box - Max Anchor (in Main Box)")]
                public string timeLeftAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Timeleft - Box - Background Color")]
                public string timeLeftBackgroundColor = "0 0 0 0";

                [JsonProperty(PropertyName = "Timeleft - Text - Min Anchor (in Timeleft Box)")]
                public string timeLeftTextAnchorMin = "0 0";

                [JsonProperty(PropertyName = "Timeleft - Text - Max Anchor (in Timeleft Box)")]
                public string timeLeftTextAnchorMax = "0.9 1";

                [JsonProperty(PropertyName = "Timeleft - Text - Text Color")]
                public string timeLeftTextColor = "0 0 0 0.9";

                [JsonProperty(PropertyName = "Timeleft - Text - Text Size")]
                public int timeLeftTextSize = 15;

                [JsonProperty(PropertyName = "Entity - Box - Min Anchor (in Main Box)")]
                public string entityAnchorMin = "0 0.71";

                [JsonProperty(PropertyName = "Entity - Box - Max Anchor (in Main Box)")]
                public string entityAnchorMax = "1 0.85";

                [JsonProperty(PropertyName = "Entity - Box - Background Color")]
                public string entityBackgroundColor = "0 0 0 0.9";

                [JsonProperty(PropertyName = "Entity - Text - Min Anchor (in Entity Box)")]
                public string entityTextAnchorMin = "0.05 0";

                [JsonProperty(PropertyName = "Entity - Text - Max Anchor (in Entity Box)")]
                public string entityTextAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Entity - Text - Text Color")]
                public string entityTextColor = "1 1 1 1";

                [JsonProperty(PropertyName = "Entity - Text - Text Size")]
                public int entityTextSize = 16;

                [JsonProperty(PropertyName = "Entity - Image - Enabled")]
                public bool entityImageEnabled = true;

                [JsonProperty(PropertyName = "Entity - Image - Min Anchor (in Entity Box)")]
                public string entityImageAnchorMin = "0.74 0";

                [JsonProperty(PropertyName = "Entity - Image - Max Anchor (in Entity Box)")]
                public string entityImageAnchorMax = "0.86 1";

                [JsonProperty(PropertyName = "Authorization Check Enabled")]
                public bool authorizationEnabled = true;

                [JsonProperty(PropertyName = "Authorization Check - Box - Min Anchor (in Main Box)")]
                public string authorizationsAnchorMin = "0 0.65";

                [JsonProperty(PropertyName = "Authorization Check - Box - Max Anchor (in Main Box)")]
                public string authorizationsAnchorMax = "1 0.71";

                [JsonProperty(PropertyName = "Authorization Check - Box - Allowed Background")]
                public string allowedBackgroundColor = "0 1 0 0.8";

                [JsonProperty(PropertyName = "Authorization Check - Box - Refused Background")]
                public string refusedBackgroundColor = "1 0 0 0.8";

                [JsonProperty(PropertyName = "Authorization Check - Text - Min Anchor (in Authorization Check Box)")]
                public string authorizationsTextAnchorMin = "0.05 0";

                [JsonProperty(PropertyName = "Authorization Check - Text - Max Anchor (in Authorization Check Box)")]
                public string authorizationsTextAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Authorization Check - Text - Text Color")]
                public string authorizationsTextColor = "1 1 1 0.9";

                [JsonProperty(PropertyName = "Authorization Check Box - Text - Text Size")]
                public int authorizationsTextSize = 14;

                [JsonProperty(PropertyName = "Price & Refund - Image Enabled")]
                public bool imageEnabled = true;

                [JsonProperty(PropertyName = "Price & Refund - Image Scale")]
                public float imageScale = 0.18f;

                [JsonProperty(PropertyName = "Price & Refund - Distance of image from right border")]
                public float rightDistance = 0.1f;

                [JsonProperty(PropertyName = "Price Enabled")]
                public bool priceEnabled = true;

                [JsonProperty(PropertyName = "Price - Box - Min Anchor (in Main Box)")]
                public string priceAnchorMin = "0 0.4";

                [JsonProperty(PropertyName = "Price - Box - Max Anchor (in Main Box)")]
                public string priceAnchorMax = "1 0.65";

                [JsonProperty(PropertyName = "Price - Box - Background Color")]
                public string priceBackgroundColor = "0 0 0 0.9";

                [JsonProperty(PropertyName = "Price - Text - Min Anchor (in Price Box)")]
                public string priceTextAnchorMin = "0.05 0";

                [JsonProperty(PropertyName = "Price - Text - Max Anchor (in Price Box)")]
                public string priceTextAnchorMax = "0.3 1";

                [JsonProperty(PropertyName = "Price - Text - Text Color")]
                public string priceTextColor = "1 1 1 0.9";

                [JsonProperty(PropertyName = "Price - Text - Text Size")]
                public int priceTextSize = 18;

                [JsonProperty(PropertyName = "Price - Text2 - Min Anchor (in Price Box)")]
                public string price2TextAnchorMin = "0.35 0";

                [JsonProperty(PropertyName = "Price - Text2 - Max Anchor (in Price Box)")]
                public string price2TextAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Price - Text2 - Text Color")]
                public string price2TextColor = "1 1 1 0.9";

                [JsonProperty(PropertyName = "Price - Text2 - Text Size")]
                public int price2TextSize = 16;

                [JsonProperty(PropertyName = "Refund Enabled")]
                public bool refundEnabled = true;

                [JsonProperty(PropertyName = "Refund - Box - Min Anchor (in Main Box)")]
                public string refundAnchorMin = "0 0.15";

                [JsonProperty(PropertyName = "Refund - Box - Max Anchor (in Main Box)")]
                public string refundAnchorMax = "1 0.4";

                [JsonProperty(PropertyName = "Refund - Box - Background Color")]
                public string refundBackgroundColor = "0 0 0 0.9";

                [JsonProperty(PropertyName = "Refund - Text - Min Anchor (in Refund Box)")]
                public string refundTextAnchorMin = "0.05 0";

                [JsonProperty(PropertyName = "Refund - Text - Max Anchor (in Refund Box)")]
                public string refundTextAnchorMax = "0.3 1";

                [JsonProperty(PropertyName = "Refund - Text - Text Color")]
                public string refundTextColor = "1 1 1 0.9";

                [JsonProperty(PropertyName = "Refund - Text - Text Size")]
                public int refundTextSize = 18;

                [JsonProperty(PropertyName = "Refund - Text2 - Min Anchor (in Refund Box)")]
                public string refund2TextAnchorMin = "0.35 0";

                [JsonProperty(PropertyName = "Refund - Text2 - Max Anchor (in Refund Box)")]
                public string refund2TextAnchorMax = "1 1";

                [JsonProperty(PropertyName = "Refund - Text2 - Text Color")]
                public string refund2TextColor = "1 1 1 0.9";

                [JsonProperty(PropertyName = "Refund - Text2 - Text Size")]
                public int refund2TextSize = 16;
            }

            [JsonProperty(PropertyName = "Remove Info (Refund & Price)")]
            public RemoveInfo removeInfo = new RemoveInfo();

            public class RemoveInfo
            {
                [JsonProperty(PropertyName = "Price Enabled")]
                public bool priceEnabled = true;

                [JsonProperty(PropertyName = "Refund Enabled")]
                public bool refundEnabled = true;

                [JsonProperty(PropertyName = "Refund Items In Entity Slot")]
                public bool refundSlot = true;

                [JsonProperty(PropertyName = "Allowed Building Grade")]
                public Dictionary<BuildingGrade.Enum, bool> validConstruction = new Dictionary<BuildingGrade.Enum, bool>();

                [JsonProperty(PropertyName = "Building Blocks Settings")]
                public Dictionary<string, BBData> buildingS = new Dictionary<string, BBData>();

                public class BBData
                {
                    [JsonProperty(PropertyName = "Display Name")]
                    public string displayName;

                    [JsonProperty(PropertyName = "Building Grade")]
                    public Dictionary<BuildingGrade.Enum, RData> buildingGrade = new Dictionary<BuildingGrade.Enum, RData>();

                    public class RData
                    {
                        [JsonProperty(PropertyName = "Price")]
                        public object price;

                        [JsonProperty(PropertyName = "Refund")]
                        public object refund;
                    }
                }

                [JsonProperty(PropertyName = "Other Entity Settings")]
                public Dictionary<string, EntityS> entityS = new Dictionary<string, EntityS>();

                public class EntityS
                {
                    [JsonProperty(PropertyName = "Remove Allowed")]
                    public bool enabled = false;

                    [JsonProperty(PropertyName = "Display Name")]
                    public string displayName = string.Empty;

                    [JsonProperty(PropertyName = "Price")]
                    public Dictionary<string, int> price = new Dictionary<string, int>();

                    [JsonProperty(PropertyName = "Refund")]
                    public Dictionary<string, int> refund = new Dictionary<string, int>();
                }
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                    LoadDefaultConfig();
            }
            catch
            {
                PrintError("The configuration file is corrupted");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData();
        }

        protected override void SaveConfig() => Config.WriteObject(configData);

        #endregion ConfigurationFile

        #region LanguageFile

        private void Print(BasePlayer player, string message) => Player.Message(player, message, $"<color={configData.chatS.prefixColor}>{configData.chatS.prefix}</color>", configData.chatS.steamIDIcon);

        private void Print(ConsoleSystem.Arg arg, string message)
        {
            if (arg?.Player() == null) Puts(message);
            else PrintToConsole(arg.Player(), message);
        }

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "You don't have '{0}' permission to use this command.",
                ["TargetDisabled"] = "{0}'s remover tool has been disabled.",
                ["TargetEnabled"] = "{0} is now using Remover Tool; Enabled for {1} seconds (Max Removeable Objects: {2},Remove Type: {3}).",
                ["ToolDisabled"] = "Your remover tool has been disabled.",
                ["ToolEnabled"] = "Now has remover tool enabled for {0} seconds (Max Removeable Objects: {1},Remove Type: {2}).",
                ["Cooldown"] = "You need to wait {0} seconds to use the remover tool again",
                ["PlayerNotFound"] = "'{0}' cannot be found or is offline.",
                ["CurrentlyDisabled"] = "remover tool is currently disabled.",
                ["EntityLimit"] = "You have removed {0} entities, remover tool is automatically disabled",

                ["StartRemoveAll"] = "Start running RemoveAll, please wait",
                ["StartRemoveStructure"] = "Start running RemoveStructure, please wait",
                ["AlreadyRemoveAll"] = "There is already a RemoveAll running, please wait",
                ["AlreadyRemoveStructure"] = "There is already a RemoveStructure running, please wait",
                ["CompletedRemoveAll"] = "You successfully removed {0} entities using RemoveAll",
                ["CompletedRemoveStructure"] = "You successfully removed {0} entities using RemoveStructure",

                ["CanRemove"] = "You can remove this entity",
                ["NotEnoughCost"] = "Can't remove: You don't have enough resources.",
                ["EntityDisabled"] = "Can't remove: Server has disabled the entity from being removed.",
                ["DamagedEntity"] = "Can't remove: Server has disabled damaged objects from being removed.",
                ["BeBlocked"] = "Can't remove: An external plugin blocked the usage",
                ["InvalidEntity"] = "Can't remove: No valid entity targeted",
                ["NotFoundOrFar"] = "Can't remove: The entity is not found or too far.",
                ["StorageNotEmpty"] = "Can't remove: The entity storage is not empty.",
                ["RaidBlocked"] = "Can't remove: Blocked by raid for {0} seconds.",
                ["NotRemoveAccess"] = "Can't remove: You don't have any rights to remove this.",
                ["NotStructure"] = "Can't remove: The entity is not a structure.",
                ["HasStash"] = "Can't remove: There are stashes under the foundation.",
                ["EntityTimeLimit"] = "Can't remove: The entity spawned time more than {0} seconds.",
                ["CantPay"] = "Can't remove: Paying system crashed! Contact an administrator with the time and date to help him understand what happened.",

                ["Refund"] = "Refund:",
                ["Nothing"] = "Nothing",
                ["Price"] = "Price:",
                ["Free"] = "Free",
                ["TimeLeft"] = "Timeleft: {0}s\nRemoved: {1}",
                ["RemoverToolType"] = "Remover Tool ({0})",
                ["Unlimit"] = "∞",

                ["SyntaxError"] = "Syntax error, please enter '<color=#ce422b>/{0} <help | h></color>' to view help",
                ["Syntax"] = "<color=#ce422b>/{0} [time (seconds)]</color> - Enable remover tool ({1})",
                ["Syntax1"] = "<color=#ce422b>/{0} <admin | a> [time (seconds)]</color> - Enable remover tool ({1})",
                ["Syntax2"] = "<color=#ce422b>/{0} <all> [time (seconds)]</color> - Enable remover tool ({1})",
                ["Syntax3"] = "<color=#ce422b>/{0} <structure | s> [time (seconds)]</color> - Enable remover tool ({1})",
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "您没有 '{0}' 权限来使用该命令",
                ["TargetDisabled"] = "'{0}' 的拆除工具已禁用",
                ["TargetEnabled"] = "'{0}' 的拆除工具已启用 {1} 秒 (可拆除数: {2}, 拆除模式: {3}).",
                ["ToolDisabled"] = "您的拆除工具已禁用",
                ["ToolEnabled"] = "您的拆除工具已启用 {0} 秒 (可拆除数: {1}, 拆除模式: {2}).",
                ["Cooldown"] = "您需要等待 {0} 秒才可以再次使用拆除工具",
                ["PlayerNotFound"] = "玩家 '{0}' 未找到或者不在线",
                ["CurrentlyDisabled"] = "服务器当前已禁用了拆除工具",
                ["EntityLimit"] = "您已经拆除了 '{0}' 个实体，拆除工具已自动禁用",

                ["StartRemoveAll"] = "开始运行 '所有拆除'，请您等待",
                ["StartRemoveStructure"] = "开始运行 '建筑拆除'，请您等待",
                ["AlreadyRemoveAll"] = "已经有一个 '所有拆除' 正在运行，请您等待",
                ["AlreadyRemoveStructure"] = "已经有一个 '建筑拆除' 正在运行，请您等待",
                ["CompletedRemoveAll"] = "您使用 '所有拆除' 成功拆除了 {0} 个实体",
                ["CompletedRemoveStructure"] = "您使用 '建筑拆除' 成功拆除了 {0} 个实体",

                ["CanRemove"] = "您可以拆除该实体",
                ["NotEnoughCost"] = "无法拆除该实体: 拆除所需资源不足",
                ["EntityDisabled"] = "无法拆除该实体: 服务器已禁用拆除这种实体",
                ["DamagedEntity"] = "无法拆除该实体: 服务器已禁用拆除已损坏的实体",
                ["BeBlocked"] = "无法拆除该实体: 其他插件阻止您拆除该实体",
                ["InvalidEntity"] = "无法拆除该实体: 无效的实体",
                ["NotFoundOrFar"] = "无法拆除该实体: 没有找到实体或者距离太远",
                ["StorageNotEmpty"] = "无法拆除该实体: 该实体内含有物品",
                ["RaidBlocked"] = "无法拆除该实体: 拆除工具被突袭阻止了 {0} 秒",
                ["NotRemoveAccess"] = "无法拆除该实体: 您无权拆除该实体",
                ["NotStructure"] = "无法拆除该实体: 该实体不是建筑物",
                ["HasStash"] = "无法拆除该实体: 地基下藏有小藏匿",
                ["EntityTimeLimit"] = "无法拆除该实体: 该实体的存活时间大于 {0} 秒",
                ["CantPay"] = "无法拆除该实体: 支付失败，请联系管理员，告诉他详情",

                ["Refund"] = "退还:",
                ["Nothing"] = "没有",
                ["Price"] = "价格:",
                ["Free"] = "免费",
                ["TimeLeft"] = "剩余时间: {0}s\n已拆除数: {1} ",
                ["RemoverToolType"] = "拆除工具 ({0})",
                ["Unlimit"] = "∞",

                ["SyntaxError"] = "语法错误，输入 '<color=#ce422b>/{0} <help | h></color>' 查看帮助",
                ["Syntax"] = "<color=#ce422b>/{0} [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax1"] = "<color=#ce422b>/{0} <admin | a> [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax2"] = "<color=#ce422b>/{0} <all> [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax3"] = "<color=#ce422b>/{0} <structure | s> [time (seconds)]</color> - 启用拆除工具 ({1})",
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}