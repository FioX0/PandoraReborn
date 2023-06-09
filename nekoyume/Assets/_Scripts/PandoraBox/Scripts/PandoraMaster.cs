using UnityEngine;
using DG.Tweening;
using UnityEngine.Audio;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using TMPro;
using Nekoyume.State;
using Libplanet.Action;
//using PlayFab.ClientModels;
using Nekoyume.Model.BattleStatus;
using UniRx;
using Nekoyume.UI;
using Nekoyume.Model.State;
using Libplanet;
using Cysharp.Threading.Tasks;
using System.Threading.Tasks;
using System.Linq;

namespace Nekoyume.PandoraBox
{
    public class PandoraMaster : MonoBehaviour
    {
        public static PandoraMaster Instance;

        //Unsaved Reg Settings
        public static string PandoraAddress = "0x1012041FF2254f43d0a938aDF89c3f11867A2A58";
        public static string OriginalVersionId = "v100371";
        public static string VersionId = "010093";

        //Pandora Database
        public static PanDatabase PanDatabase;
        public static Guild CurrentGuild; //data for local player since we use it alot
        public static GuildPlayer CurrentGuildPlayer; //data for local player since we use it alot

        //Playfab
        //public static Dictionary<string, UserDataRecord> PlayFabUserData = new Dictionary<string, UserDataRecord>();

        //General
        public static float WncgPrice = 0;
        public static string CrystalTransferTx = "";
        public static PandoraUtil.ActionType CurrentAction = PandoraUtil.ActionType.Idle;
        public static int ActionCooldown = 4;
        public static bool MarketPriceHelper = false;
        public static string MarketPriceValue;
        public static int SelectedLoginAccountIndex;
        public static int ArenaTicketsToUse = 1;
        public static List<string> ArenaFavTargets = new List<string>();
        public static int FavItemsMaxCount = 15;
        public static List<string> FavItems = new List<string>();
        public static bool IsRankingSimulate; //simulate ranking battle
        public static bool IsHackAndSlashSimulate; //simulate h&s
        public static BattleLog CurrentBattleLog; //current stage log
        public static int SelectedWorldID; // pve simulate
        public static int SelectedStageID; // pve simulate
        public static string CurrentArenaEnemyAddress;
        public static Model.State.AvatarState CurrentShopSellerAvatar; //selected item owner avatar
        public static bool IsMultiCombine;


        //Objects
        public PandoraSettings Settings;

        //Inventory
        public static List<Model.Item.ItemBase> TestShopItems = new List<Model.Item.ItemBase>();

        //Skins
        public static Color StickManOutlineColor;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                Settings = new PandoraSettings();
                Settings.Load();
                //StartCoroutine(PandoraDB.GetDatabase());
                //StartCoroutine(Premium.PANDORA_ProcessQueueWebHook());
            }
        }

        public void ShowError(int errorNumber)
        {
            Widget.Find<PandoraError>().Show($"Error <color=red>{errorNumber}</color>!",
                PandoraUtil.GetNotificationText(errorNumber));
        }
    }

    public class PandoraSettings
    {
        //General
        [HideInInspector] public bool IsStory { get; set; } = false;
        [HideInInspector] public bool IsMultipleLogin { get; set; } = false;
        [HideInInspector] public int BlockShowType { get; set; } = 0;
        [HideInInspector] public int CurrencyType { get; set; } = 2;
        [HideInInspector] public bool RandomNode { get; set; } = true;

        //PVE
        [HideInInspector] public int FightSpeed { get; set; } = 1;
        [HideInInspector] public int RaidCooldown { get; set; } = 30;

        //PVP

        [HideInInspector] public int ArenaListUpper { get; set; } = 0;

        [HideInInspector] public int ArenaListLower { get; set; } = 0;

        [HideInInspector] public int ArenaListStep { get; set; } = 90;

        [HideInInspector]
        public bool ArenaPush { get; set; } = true; //push means send every 'ArenaPushStep' whatever its confirm or not

        [HideInInspector] public int ArenaPushStep { get; set; } = 5;
        [HideInInspector] public bool ArenaValidator { get; set; } = true; //true = 9cscan, false = local node

        public void Save()
        {
            //General
            PlayerPrefs.SetString("_PandoraBox_Ver", PandoraMaster.VersionId);
            PlayerPrefs.SetInt("_PandoraBox_General_IsStory", System.Convert.ToInt32(IsStory));
            PlayerPrefs.SetInt("_PandoraBox_General_BlockShowType", BlockShowType);
            PlayerPrefs.SetInt("_PandoraBox_General_CurrencyType", CurrencyType);
            PlayerPrefs.SetInt("_PandoraBox_General_RandomNode", System.Convert.ToInt32(RandomNode));

            //PVE
            PlayerPrefs.SetInt("_PandoraBox_PVE_FightSpeed", FightSpeed);
            PlayerPrefs.SetInt("_PandoraBox_PVE_RaidCooldown", RaidCooldown);

            //PVP
            PlayerPrefs.SetInt("_PandoraBox_PVP_ListCountLower", ArenaListLower);
            PlayerPrefs.SetInt("_PandoraBox_PVP_ListCountUpper", ArenaListUpper);
            PlayerPrefs.SetInt("_PandoraBox_PVP_ListCountStep", ArenaListStep);
            PlayerPrefs.SetInt("_PandoraBox_PVP_ArenaPush", System.Convert.ToInt32(ArenaPush));
            PlayerPrefs.SetInt("_PandoraBox_PVP_ArenaPushStep", ArenaPushStep);
            PlayerPrefs.SetInt("_PandoraBox_PVP_ArenaValidator", System.Convert.ToInt32(ArenaValidator));
        }

        public void Load()
        {
            if (!PlayerPrefs.HasKey("_PandoraBox_Ver"))
            {
                Save();
                return;
            }

            //check difference
            if (int.Parse(PandoraMaster.VersionId.Substring(0, 5)) >
                int.Parse(PlayerPrefs.GetString("_PandoraBox_Ver").Substring(0, 5)))
            {
                PlayerPrefs.SetString("_PandoraBox_Ver", PandoraMaster.VersionId);
                //PlayerPrefs.SetInt("_PandoraBox_General_WhatsNewShown", 0); //false

                PlayerPrefs.SetInt("_PandoraBox_General_IsStory", System.Convert.ToInt32(true));
            }

            //General
            IsStory = System.Convert.ToBoolean(PlayerPrefs.GetInt("_PandoraBox_General_IsStory",
                System.Convert.ToInt32(IsStory)));
            BlockShowType = PlayerPrefs.GetInt("_PandoraBox_General_BlockShowType", BlockShowType);
            CurrencyType = PlayerPrefs.GetInt("_PandoraBox_General_CurrencyType", CurrencyType);
            RandomNode = System.Convert.ToBoolean(PlayerPrefs.GetInt("_PandoraBox_General_RandomNode",
                System.Convert.ToInt32(RandomNode)));

            //PVE
            FightSpeed = PlayerPrefs.GetInt("_PandoraBox_PVE_FightSpeed", FightSpeed);
            RaidCooldown = PlayerPrefs.GetInt("_PandoraBox_PVE_RaidCooldown", RaidCooldown);

            //PVP
            ArenaListUpper = PlayerPrefs.GetInt("_PandoraBox_PVP_ListCountUpper", ArenaListUpper);
            ArenaListLower = PlayerPrefs.GetInt("_PandoraBox_PVP_ListCountLower", ArenaListLower);
            ArenaListStep = PlayerPrefs.GetInt("_PandoraBox_PVP_ListCountStep", ArenaListStep);
            ArenaPush = System.Convert.ToBoolean(PlayerPrefs.GetInt("_PandoraBox_PVP_ArenaPush",
                System.Convert.ToInt32(ArenaPush)));
            ArenaPushStep = PlayerPrefs.GetInt("_PandoraBox_PVP_ArenaPushStep", ArenaPushStep);
            ArenaValidator = System.Convert.ToBoolean(PlayerPrefs.GetInt("_PandoraBox_PVP_ArenaValidator",
                System.Convert.ToInt32(ArenaValidator)));
        }
    }
}
