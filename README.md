# RemoverTool

Oxide plugin for Rust. Building and entity removal tool

## Features

* Player and Admin remover tool
* Refund option
* Pay option
* Use Tool Cupboard or/and Building Owners or/and Entity Builder (see under)
* Remove Structures
* Remove Deployables
* Choose what entities may be removed
* Supports most Clans plugins
* Supports NoEscape
* GUI

## Permissions

> This plugin uses the permission system. To assign a permission, use `oxide.grant <user or group> <name or steam id> <permission>`. To remove a permission, use `oxide.revoke <user or group> <name or steam id> <permission>`.

There are no restrictions on all, structure, and external types. Please do not grant them to players

* `removertool.normal` - Required to use normal remove
* `removertool.admin` - Required to use admin remove. In this mode, any entity can be removed.
* `removertool.all` - Required to use all remove.
* `removertool.structure` - Required to use structure remove.
* `removertool.external` - Required to use external remove.
* `removertool.target` - Required to use 'remove.target' command
* `removertool.override` - Required to use 'remove.allow' command

## Chat Commands

* `/remove [time (seconds)]` -- Enable/Disable RemoverTool
* `/remove <admin | a> [time (seconds)]` -- Enable admin RemoverTool. In this mode, any entity can be removed.
* `/remove all [time (seconds)]` -- Remove everything that touchs each other starting where you are looking at (will remove multiple buildings if they are too close to each other) (might be slow for big buildings)
* `/remove <structure | s> [time (seconds)]` -- Remove an entire building (won't remove buildings that are close to eachother or deployables) (VERY fast even on big buildings)
* `/remove <external | e> [time (seconds)]` -- Remove adjacent high external walls
* `/remove <help | h>` -- View help

## Console Commands

* `remove.toggle` -- Same as /remove, Can only be used in gaming F1 consoles
* `remove.target <disable | d> <player (name or id)>` -- Disable remover tool for player
* `remove.target <normal | n> <player (name or id)> [time (seconds)] [max removable objects (integer)]` -- Enable remover tool for player (Normal)
* `remove.target <admin | a> <player (name or id)> [time (seconds)]` -- Enable remover tool for player (Admin)
* `remove.target <all> <player (name or id)> [time (seconds)]` -- Enable remover tool for player (All)
* `remove.target <structure | s> <player (name or id)> [time (seconds)]` -- Enable remover tool for player (Structure)
* `remove.target <external | e> <player (name or id)> [time (seconds)]` -- Enable remover tool for player (External)
* `remove.playerentity <all | a> <player id>` -- Remove all entities of the player
* `remove.playerentity <building | b> <player id>` -- Remove all buildings of the player
* `remove.playerentity <cupboard | c> <player id>` -- Remove buildings of the player owned cupboard
* `remove.allow <false/true>` -- override the remover tool for players, If you set it to false, only players with the "removertool.override" permission can use the remover tool. This is NOT saved after a server restart or plugin restart!!! This is for use with **Timed Executed** if you want your server to have the remover tool only during a certain period of time.
* `remove.building <price / refund> <percentage>` -- Set price or refund for all buildings. e.g. 'remove.building price 50'. Display in this way:
  ```json
  "Twigs": {
    "Price": {
      "wood": {
        "amount": 10,
        "skinId": -1
      }
    },
    "Refund": {
      "wood": {
        "amount": 10,
        "skinId": -1
      }
    }
  },
  ```
* `remove.building <priceP / refundP> <percentage>` -- Set price or refund for all buildings. e.g. 'remove.building priceP 50'. Display in this way:
  ```json
  "Twigs": {
    "Price": 50,
    "Refund": 20
  },
  ```

## Configuration

> The settings and options can be configured in the `RemoverTool` file under the `config` directory. The use of an editor and validator is recommended to avoid formatting issues and syntax errors.

### UI Image

**If you want to use Image, you must install the ImageLibrary plugin**
Images of deployable entities and items don't need to be added; ImageLibrary already has them.
If you need to add images of other entities, use the following format:
  ```json
  "Image Urls (Used to UI image)": {
    "Entity short prefab name": "Image url",
  },
  ```

### Remove Access

* **Use Tool Cupboards (Strongly unrecommended)**
This will allow TC authorized players to remove entity within the TC range. In other words, Player cannot remove the entity only when building blocked. Strongly unrecommended means that it is not recommended to use it alone
* **Use Building Locks**
It is an additional check in the tc check. So you have to enable tc checks -> "Use Tool Cupboards". When you're not blocked by the building, it checks to see if you have authed all the locks in the building.
* **Use Entity Owners**
This will allow the entity owner to delete the entity
* **Use Building Owners (You will need BuildingOwners plugin)**
This will allow remove for players that own a building (the one that built the first foundation in the base)
* **Use Friends & Use Teams & Use Clans**
Depending on what previous option you've chosen (entity owners or building owners) it will allow (friends / teammates / clan members) of these people to remove
e.g. : If both of the following are enabled. Players need to be the entity owner and have TC authorization to remove the entity
```json
"Use Entity Owners": true,
"Use Tool Cupboards (Strongly unrecommended)": true,
```
### Price / Refund

**Economics & ServerRewards**
Use it as a price or refund. e.g. :
  ```json
  "Price": {
      "scrap": {
        "amount": 30,
        "skinId": -1
      },
      "Economics": {
        "amount": 30,
        "skinId": -1
      },
      "ServerRewards": {
        "amount": 30,
        "skinId": -1
      }
  },
  "Refund": {
      "scrap": {
        "amount": 30,
        "skinId": -1
      },
      "Economics": {
        "amount": 30,
        "skinId": -1
      },
      "ServerRewards": {
        "amount": 30,
        "skinId": -1
      }
  }
  ```
**Refund by % of the initial cost**

It will **ONLY** work on Buildings, not on deployables. (So Wood, Twigs, Metal, Stones, and TopTier)
```json
"Twigs": {
  "Price": 50,  //Pay 50% of the initial cost
  "Refund": 60   //Refund 60% of the initial cost
},
```
**Remove Button**

  FORWARD,
  BACKWARD,
  LEFT,
  RIGHT,
  JUMP,
  DUCK,
  SPRINT,
  USE,
  FIRE_PRIMARY,
  FIRE_SECONDARY,
  RELOAD,
  FIRE_THIRD

**Entity Spawned Time Limit**: Because the plugin does not save the time when the entity was spawned. so this is only used for entities that are spawned after the plugin is loaded.

## Configuration

> The settings and options can be configured in the `RemoverTool` file under the `config` directory. The use of an editor and validator is recommended to avoid formatting issues and syntax errors.
```json
{
  "Settings": {
    "Use Teams": true,
    "Use Clans": true,
    "Use Friends": true,
    "Use Entity Owners": true,
    "Use Tool Cupboards (Strongly unrecommended)": false,
    "Use Building Owners (You will need BuildingOwners plugin)": false,
    "Remove Button": "FIRE_PRIMARY",
    "Remove Interval (Min = 0.2)": 0.5,
    "Only start cooldown when an entity is removed": false,
    "RemoveType - All/Structure - Remove per frame": 20,//Reducing it can reduce server lag, but take longer to remove
    "RemoveType - All/Structure - No item container dropped": true,//If false, it will drop
    "RemoveType - Normal - Max Removable Objects - Exclude admins": true,
    "RemoveType - Normal - Cooldown - Exclude admins": true,
    "RemoveType - Normal - Check stash under the foundation": false,
    "RemoveType - Normal - Entity Spawned Time Limit - Enabled": false,//If true, the spawned entity before installing the plugin cannot be removed
    "RemoveType - Normal - Entity Spawned Time Limit - Cannot be removed when entity spawned time more than it": 300.0, //Seconds
    "Default Entity Settings (When automatically adding new entities to 'Other Entity Settings')": {
      "Default Remove Allowed": true
    }
  },
  "Container Settings": {
    "Storage Container - Enable remove of not empty storages": true,
    "Storage Container - Drop items from container": false,
    "Storage Container - Drop a item container from container": true,
    "IOEntity Container - Enable remove of not empty storages": true,
    "IOEntity Container - Drop items from container": false,
    "IOEntity Container - Drop a item container from container": true
  },
  "Remove Damaged Entities": {
    "Enabled": false,
    "Exclude Building Blocks": true,
    "Percentage (Can be removed when (health / max health * 100) is not less than it)": 90.0
  },
  "Chat Settings": {
    "Chat Command": "remove",
    "Chat Prefix": "[RemoverTool]: ",
    "Chat Prefix Color": "#00FFFF",
    "Chat SteamID Icon": 0
  },
  "Permission Settings (Just for normal type)": {//You can add more permissions here. but "removertool.normal" is necessary
    "removertool.normal": {
      "Priority": 0,
      "Distance": 3.0,
      "Cooldown": 60.0,
      "Max Time": 300,
      "Remove Interval (Min = 0.2)": 1.0,
      "Max Removable Objects (0 = Unlimited)": 50,//Maximum removable object each time the remover tool is enabled
      "Pay": true,
      "Refund": true,
      "Reset the time after removing an entity": false
    },
    "removertool.vip": { // vip permission
      "Priority": 1,
      "Distance": 5.0,
      "Cooldown": 30.0,
      "Max Time": 300,
      "Remove Interval (Min = 0.2)": 0.5,
      "Max Removable Objects (0 = Unlimit)": 100,
      "Pay": false,
      "Refund": true,
      "Reset the time after removing a entity": true
    }
  },
  "Remove Type Settings": {
    "Normal": {
      "Display Name": "Normal",
      "Distance": 3.0,
      "Default Time": 60,
      "Max Time": 300,
      "Gibs": true,
      "Reset the time after removing an entity": false
    }
  },
  "Remove Mode Settings (Only one model works)": {
    "No Held Item Mode": true, //Prevents the player from holding any item
    "No Held Item Mode - Disable remover tool when you have any item in hand": true, //remover tool disabled if player holds item
    "Melee Tool Hit Mode": false,
    "Melee Tool Hit Mode - Item shortname": "hammer",
    "Melee Tool Hit Mode - Item skin (-1 = All skins)": -1,
    "Melee Tool Hit Mode - Auto enable remover tool when you hold a melee tool": false,
    "Melee Tool Hit Mode - Requires a melee tool in your hand when remover tool is enabled": false,
    "Melee Tool Hit Mode - Disable remover tool when you are not holding a melee tool": false,
    "Specific Tool Mode": false,
    "Specific Tool Mode - Item shortname": "hammer",
    "Specific Tool Mode - Item skin (-1 = All skins)": -1,
    "Specific Tool Mode - Auto enable remover tool when you hold a specific tool": false,
    "Specific Tool Mode - Requires a specific tool in your hand when remover tool is enabled": false,
    "Specific Tool Mode - Disable remover tool when you are not holding a specific tool": false
  },
  "NoEscape Settings": {
    "Use Raid Blocker": false,
    "Use Combat Blocker": false
  },
  "Image Urls (Used to UI image)": {
    "economics": "https://i.imgur.com/znPwdcv.png",
    "serverrewards": "https://i.imgur.com/04rJsV3.png"
  },
  "GUI": {  },
  "Remove Info (Refund & Price)": {
    "Price Enabled": true,
    "Refund Enabled": true,
    "Refund Items In Entity Slot": true,//What is "Entity Slot": e.g. Code lock on the cupboard
    "Allowed Building Grade": {
      "Twigs": true,  //Allow removal of Twigs
      "Wood": true,
      "Stone": true,
      "Metal": true,
      "TopTier": true
    },
    "Display Names (Refund & Price)": {
        "telephone_5566": "Telephone", // item shortname    skin id // If empty, the default item name will be displayed
        "economics": "Economics", // custom currency
    },
    "Building Blocks Settings": {
      "Foundation": {
        "Display Name": "Foundation", //The name displayed on the RemoverToolUI
        "Building Grade": {
          "Twigs": {
            "Price": {
              "wood": {
                "amount": 15,
                "skinId": -1 // If less than 0, will ignore skin to take items
              }
            },
            "Refund": {
              "wood": {
                "amount": 10,
                "skinId": -1  // If less than 0, the item skin ID will be set based on the entity skin Id
              }
            }
          }
        }
      }
    },
    "Other Entity Settings": {//If you want to add other entities, you can do so here
      "fogmachine": {  //entity short prefab name
        "Remove Allowed": true, //Allows the entity to be remove
        "Display Name": "Fogger-3000", //The name displayed on the RemoverToolUI
        "Price": {},
        "Refund": {
          "fogmachine": {
            "amount": 1,
            "skinId": -1
          }
        }
      }
    }
  }
}
```

## Hooks
```cs
private object canRemove(BasePlayer player, BaseEntity entity)
```
```cs
private void OnNormalRemovedEntity(BasePlayer player, BaseEntity entity)  // Called when deleting an entity in normal remove mode
```
```cs
private object OnDropContainerEntity(BaseEntity entity) // Called when the container is about to drop
```
```cs
private void OnRemoverToolActivated(Baseplayer player) // Called only in normal remove mode
```
```cs
private void OnRemoverToolDeactivated(Baseplayer player) // Called only in normal remove mode
```
## Custom entity removal settings

Custom entity removal settings via external plugins, in other words, you can modify the price of the entity or the refunded items through other plugins
An example is given below (remove the drone via removertool and refund the drone item)
```cs
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Remover Tool API Example", "Arainrr", "1.0.0")]
    [Description("Example of api usage for the Remover Tool plugin")]
    public class RemoverToolAPIExample : RustPlugin
    {
        private class RemovableEntityInfo
        {
            /// <summary>
            /// Id of the entity image.
            /// </summary>
            public string ImageId { get; set; }

            /// <summary>
            /// Display name of the entity.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// Remove the price of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Price { get; set; }

            /// <summary>
            /// Remove the refund of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Refund { get; set; }

            public struct ItemInfo
            {
                /// <summary>
                /// Amount of the item.
                /// </summary>
                public int Amount { get; set; }

                /// <summary>
                /// SkinId of the item.
                /// Less than 0 is not specified skin.
                /// </summary>
                public long SkinId { get; set; }

                /// <summary>
                /// Id of the item image.
                /// </summary>
                public string ImageId { get; set; }

                /// <summary>
                /// Display name of the item.
                /// </summary>
                public string DisplayName { get; set; }
            }
        }

        private const string RefundItemName = "Drone Item"; // Make sure the custom ItemName is unique.
        private const string PriceItemName = "Custom Currency"; // Make sure the custom ItemName is unique.

        private readonly Dictionary<string, object> _droneEntityInfo = new Dictionary<string, object>
        {
            ["DisplayName"] = "Drone",
            ["Price"] = new Dictionary<string, object>
            {
                // All items are built-in.
                ["wood"] = new Dictionary<string, object>
                {
                    ["Amount"] = 1000,
                    ["DisplayName"] = "Wood..."
                },
                // economics and serverrewards are built-in not custom.
                ["economics"] = new Dictionary<string, object>
                {
                    ["Amount"] = 100,
                    ["DisplayName"] = "Economics..."
                },
                // Custom ItemName.
                [PriceItemName] = new Dictionary<string, object>
                {
                    ["Amount"] = 100,
                    ["DisplayName"] = "Custom Gold"
                }
            },
            ["Refund"] = new Dictionary<string, object>
            {
                // Custom ItemName.
                [RefundItemName] = new Dictionary<string, object>
                {
                    ["Amount"] = 1,
                    ["DisplayName"] = "Refund Drone",
                },
                // Custom SkinId
                ["box.wooden.large"] = new Dictionary<string, object>
                {
                    ["Amount"] = 1,
                    ["SkinId"] = 1742653197L,
                    ["DisplayName"] = "MiniCopter",
                }
            }
        };

        #region RemoverTool Hooks

        /// <summary>
        /// Used to check if the player can pay. It is only called when there is a custom ItemName
        /// in the price
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <param name="itemName"> Item name </param>
        /// <param name="itemAmount"> Item amount </param>
        /// <param name="skinId"> Less than 0 is not specified skin </param>
        /// <param name="check"> If true, check if the player can pay. If false, consume the item </param>
        /// <returns> Returns whether payment can be made or whether payment was successful </returns>
        private bool OnRemovableEntityCheckOrPay(BaseEntity entity, BasePlayer player, string itemName, int itemAmount, long skinId, bool check)
        {
            PrintWarning($"OnRemovableEntityCheckOrPay: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount} | {skinId} | {check}");
            if (itemName == PriceItemName)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Called when giving refund items. It is only called when there is a custom item name in
        /// the refund.
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <param name="itemName"> Item name </param>
        /// <param name="itemAmount"> Item amount </param>
        /// <param name="skinId"> Less than 0 is not specified skin </param>
        /// <returns> Returns whether the refund has been granted successful </returns>
        private bool OnRemovableEntityGiveRefund(BaseEntity entity, BasePlayer player, string itemName, int itemAmount, long skinId)
        {
            PrintWarning($"OnRemovableEntityGiveRefund: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount} | {skinId}");
            if (itemName == RefundItemName)
            {
                var item = ItemManager.CreateByName("drone", itemAmount);
                player.GiveItem(item);
            }

            return true;
        }

        /// <summary>
        /// Return information about the removable entity.
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <returns> Serialized information </returns>
        private Dictionary<string, object> OnRemovableEntityInfo(BaseEntity entity, BasePlayer player)
        {
            PrintWarning($"OnRemovableEntityInfo: {entity.ShortPrefabName} | {player.userID}");
            if (entity is Drone)
            {
                return _droneEntityInfo;
            }

            return null;
        }

        #endregion RemoverTool Hooks
    }
}
```
## Credits

**Arainrr**: Previous maintainer