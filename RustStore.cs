using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Libraries;
using System.Text;
using System.Linq;
using System.IO;
using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Configuration;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info(CPluginInfo.Title, CPluginInfo.Author, "1.21.41")]
    [Description("Moscow.ovh rust store")]
    public class RustStore : RustPlugin
    {
        private static class CPluginInfo
        {
            internal const string Name = "RustStore";
            internal const string Author = "Bombardir, Moscow.OVH";
            internal const string Title = "RustStore";
        }

        private static class CLog
        {
            public static void File(string text, string title = "info")
            {
                Instance.LogToFile(title, text, Instance);
            }

            public static void LogErrorWithFile(string text)
            {
                Error(text);
                File(text, "errors");
            }

            public static void Debug(string format, params object[] args)
            {
                UnityEngine.Debug.Log($"[{CPluginInfo.Name}] {string.Format(format, args)}");
            }

            public static void Error(string format, params object[] args)
            {
                UnityEngine.Debug.LogError($"[{CPluginInfo.Name}] {string.Format(format, args)}");
            }

            public static void Warning(string format, params object[] args)
            {
                UnityEngine.Debug.LogWarning($"[{CPluginInfo.Name}] {string.Format(format, args)}");
            }

            public static void Debug(string format)
            {
                UnityEngine.Debug.Log($"[{CPluginInfo.Name}] {format}");
            }

            public static void Error(string format)
            {
                UnityEngine.Debug.LogError($"[{CPluginInfo.Name}] {format}");
            }

            public static void Warning(string format)
            {
                UnityEngine.Debug.LogWarning($"[{CPluginInfo.Name}] {format}");
            }
        }

        public class CQueueImageCache
        {
            public delegate void OnDownloadCompleteHandler(string id);

            private class QueueItem
            {
                internal readonly string ImageUrl;
                internal event OnDownloadCompleteHandler OnDownloadComplete;
                internal UnityWebRequest Www;

                internal QueueItem(string imageUrl, OnDownloadCompleteHandler action)
                {
                    ImageUrl = imageUrl;
                    if (action != null)
                    {
                        OnDownloadComplete += action;
                    }
                }

                internal void Invoke(string id)
                {
                    OnDownloadComplete?.Invoke(id);
                }
            }

            public int MaxActiveDownloads = 1;

            private readonly HashSet<QueueItem> _activeDownloads = new HashSet<QueueItem>();
            private readonly Queue<QueueItem> _queueList = new Queue<QueueItem>();
            private readonly Dictionary<string, string> _cachedImages = new Dictionary<string, string>();

            public void CacheImage(string imageUrl, OnDownloadCompleteHandler onDownloadComplete = null)
            {
                var itemCopy = _queueList.FirstOrDefault(item => item.ImageUrl == imageUrl) ??
                                     _activeDownloads.FirstOrDefault(item => item.ImageUrl == imageUrl);

                // Check if already queueing or downloading image
                if (itemCopy != null)
                {
                    if (onDownloadComplete != null)
                    {
                        itemCopy.OnDownloadComplete += onDownloadComplete;
                    }

                    return;
                }

                _queueList.Enqueue(new QueueItem(imageUrl, onDownloadComplete));
                NextQueue();
            }

            public bool GetCachedImage(string url, out string id) => _cachedImages.TryGetValue(url, out id);

            private void NextQueue()
            {
                // Check if we can start download of next image
                if (_queueList.Count == 0 || _activeDownloads.Count >= MaxActiveDownloads)
                {
                    return;
                }

                Rust.Global.Runner.StartCoroutine(WaitForRequest(_queueList.Dequeue()));
            }

            private static void PostImageError(string error, string url)
            {
                var data = StoreApi.ImagaErrorData;
                var msg = $"Url: '{url}', Error: {error}";
                data["message"] = msg;
                CLog.Error("Не удалось скачать изображение: " + msg);
                CStoreApi.PostRequest(data);
            }

            private IEnumerator WaitForRequest(QueueItem queueItem)
            {
                // Starting download
                _activeDownloads.Add(queueItem);
                var imageUrl = queueItem.ImageUrl;
                var www = UnityWebRequestTexture.GetTexture(imageUrl);
                queueItem.Www = www;
                yield return www.Send();

                _activeDownloads.Remove(queueItem);

                // Check that image downloaded successfully
                if (!string.IsNullOrEmpty(www.error))
                {
                    PostImageError(www.error, imageUrl);
                    queueItem.Invoke(null);
                    NextQueue();
                    yield break;
                }

                // Check that data is image
                var tex = DownloadHandlerTexture.GetContent(www);
                if (tex == null || tex.height == 8 && tex.width == 8 && tex.name == string.Empty && tex.anisoLevel == 1)
                {
                    PostImageError("Неправильный формат изображения", imageUrl);
                    queueItem.Invoke(null);
                    NextQueue();
                    yield break;
                }

                // Store image on server
                var bytes = www.downloadHandler.data;
                var id = FileStorage.server.Store(bytes, FileStorage.Type.png, 0);
                if (FileStorage.server.Get(id, FileStorage.Type.png, 0) == null)
                {
                    PostImageError("Ошибка при сохранении изображения в базу сервера", imageUrl);
                    queueItem.Invoke(null);
                    NextQueue();
                    yield break;
                }

                var idStr = id.ToString();

                _cachedImages[imageUrl] = idStr;

                // Calling callback
                queueItem.Invoke(idStr);

                NextQueue();
            }

            public void Dispose()
            {
                foreach (var obj in _activeDownloads)
                {
                    obj.Www.Dispose();
                }

                _activeDownloads.Clear();
                _cachedImages.Clear();
                _queueList.Clear();
            }
        }

        public enum EItemType
        {
            Blueprint = 5,
            GameItem = 2,
            Commands = 1
        }

        public class CStoreCache
        {
            private float _lastTimeGivingAll;

            public bool IsGivingAll() => UnityEngine.Time.realtimeSinceStartup - _lastTimeGivingAll <= 30;

            public void MarkGivingAll()
            {
                _lastTimeGivingAll = UnityEngine.Time.realtimeSinceStartup;
            }

            public void MarkGivedAll()
            {
                _lastTimeGivingAll = 0;
            }

            public readonly Dictionary<string, CItemJson> CachedCart = new Dictionary<string, CItemJson>();
        }

        public class CJsonResponse
        {
            private const string SuccessResp = "success";

            public string rawResponse;
            public string message = "empty";
            public string status = "empty";
            public JToken data;

            public bool isSuccess() => status == SuccessResp;
            public bool isFailure() => status != SuccessResp;
        }

        public class CRawStoreItem
        {
            public string pid;
            public string queueID;
            public string icon;

            public virtual void MarkGived()
            {
            }
        }

        public class CStoreItem<T> : CRawStoreItem
        {
            public T data;
        }

        public class CItemJson : CStoreItem<JToken>
        {
            private float lastTimeGived;
            public EItemType type;

            public bool isGived() => lastTimeGived > 0;
            public override void MarkGived() => lastTimeGived = UnityEngine.Time.realtimeSinceStartup;
            public bool CanBeRemoved() => UnityEngine.Time.realtimeSinceStartup - lastTimeGived > 30;
        }

        public class CAutoItemJson : CStoreItem<string[]>
        {
            public string steamID;
        }

        public class UberItem
        {
            public readonly string Shortname;
            public readonly ulong SkinId;

            public UberItem(string shortname, ulong skinId)
            {
                Shortname = shortname;
                SkinId = skinId;
            }
        }

        public class CWwwRequests
        {
            private readonly List<UnityWebRequest> _activeRequests = new List<UnityWebRequest>();

            public void Request(string url, Dictionary<string, string> data = null,
                Action<string, string> onRequestComplete = null)
            {
                Rust.Global.Runner.StartCoroutine(WaitForRequest(url, data, onRequestComplete));
            }

            private IEnumerator WaitForRequest(string url, Dictionary<string, string> data = null,
                Action<string, string> onRequestComplete = null)
            {
                var www = data == null ? UnityWebRequest.Get(url) : UnityWebRequest.Post(url, data);

                _activeRequests.Add(www);

                yield return www.Send();

                onRequestComplete?.Invoke(www.downloadHandler.text, www.error);

                _activeRequests.Remove(www);
            }

            public void Dispose()
            {
                foreach (var www in _activeRequests)
                {
                    try
                    {
                        www.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        public class CStoreConfig
        {
            private readonly Dictionary<string, string> _messages = new Dictionary<string, string>()
            {
                ["GIVE.MIND"] = "Ваш персонаж должен быть в сознании",
                ["GIVE.CUPBOARD.OTHER"] = "Вы не можете получить товар в зоне чужого шкафа",
                ["GIVE.CUPBOARD.OWNER"] = "Вы должны находится в зоне своего шкафа, чтобы получить товар",

                ["STORE.UNAVAILABLE"] = "Магазин временно недоступен",
                ["STORE.ERROR"] = "Что-то пошло не так...",
                ["STORE.OFFLINE"] = "Магазин выключен",
                ["STORE.RELOAD"] = "Магазин обновляется, повторите попытку позднее",

                ["ITEM.BLOCKED"] = "Предмет заблокирован",
                ["ITEM.ALREADYGIVED"] = "Предмет уже был выдан",

                ["ITEMS.EMPTY"] = "Товары отсутствуют",
                ["ITEMS.GIVE"] = "Ожидайте, ваши товары выдаются",
                ["ITEMS.GIVEN"] = "Все товары успешно выданы",

                ["IMAGE.LOADING"] = "Изображение\nзагружается",
                ["IMAGE.MISSED"] = "Изображения\nнет",

                ["SLOTS.RELEASE"] = "Освободите {SLOTS}\n для получения {ITEM.NAME} x {AMOUNT}",
                ["SLOTS.DECLENSION1"] = "слот",
                ["SLOTS.DECLENSION2"] = "слота",
                ["SLOTS.DECLENSION3"] = "слотов",

                ["STORE.NOSTEAM.SYNTAX"] = "Ошибка синтаксиса: /store login \"TOKEN\"",
                ["STORE.NOSTEAM.TOKEN.INVALID"] = "Вы ввели неправильный токен",
                ["STORE.NOSTEAM.TOKEN.EXPIRED"] = "Введенный токен устарел",
                ["STORE.NOSTEAM.REGISTERED"] = "Пользователь с таким steamid уже зарегистрирован",
                ["STORE.NOSTEAM.SUCCESS"] =
                    "Авторизация прошла успешно, теперь вы можете пользоваться корзиной в течение часа"
            };

            [JsonProperty("Включить SSL (шифрование трафика)")]
            public bool EnableSsl = false;

            [JsonProperty("номер магазина")] public string StoreId = "0";

            [JsonProperty("номер сервера")] public string ServerId = "0";

            [JsonProperty("ключ сервера")] public string ApiKey = "key";

            [JsonProperty("выдача в один слот")] public bool StackItemsInOneSLot = false;

            [JsonProperty("модификатор прочности раскаленных предметов")]
            public float UberModifier = 1f;

            [JsonProperty("иконка корзины")] public string CustomStoreIcon = "";

            [JsonProperty("позиция иконки по вертикали (0.0 - 1.0)")]
            public float IconPosY = 0.005f;

            [JsonProperty("высота иконки (0.0 - 1.0)")]
            public float IconHeight = 0.045f;

            [JsonProperty("позиция иконки по горизонтали (0.0 - 1.0)")]
            public float IconPosX = 0.005f;

            [JsonProperty("ширина иконки (0.0 - 1.0)")]
            public float IconWidth = 0.025f;

            [JsonProperty("показывать иконку магазина")]
            public bool ShowIcon = true;

            [JsonProperty("включить англоязычную версию GUI")]
            public bool UseEnglishImages = false;

            [JsonProperty("выдача только в зоне своих шкафов")]
            public bool OnlyCupboardGive = false;

            [JsonProperty("запрет выдачи в зоне чужих шкафов")]
            public bool BanOtherCupboardGive = true;

            private DynamicConfigFile configSource;

            public void InitializeMessages()
            {
                Lang.RegisterMessages(_messages, Instance);
            }

            public void Save()
            {
                configSource.WriteObject(this);
            }

            public static CStoreConfig Parse(DynamicConfigFile pluginConfig)
            {
                CStoreConfig output = null;

                // Parsing config
                try
                {
                    output = pluginConfig.ReadObject<CStoreConfig>();
                }
                catch
                {
                    // ignored
                }

                if (output == null)
                {
                    pluginConfig.Save($"{pluginConfig.Filename}.jsonError");

                    output = new CStoreConfig();

                    CLog.Error("Файл конфигурации \"{0}\" содержит ошибку и был заменен на стандартный.\n" +
                               "Ошибочный файл конфигурации сохранен под названием \"{0}.jsonError\"", CPluginInfo.Name);
                }

                output.configSource = pluginConfig;

                if (output.UberModifier > 5)
                {
                    output.UberModifier = 5;
                    CLog.Error("Модификатор прочности раскаленных предметов не может быть больше 5.");
                }

                if (output.UberModifier <= 0)
                {
                    output.UberModifier = 1;
                    CLog.Error("Модификатор прочности раскаленных предметов не может быть меньше 0.");
                }

                pluginConfig.WriteObject(output);

                return output;
            }
        }

        public class CGameItemJson
        {
            public string itemName;
            public int quantity;
            public bool block;
        }

        public class CStoreApi
        {
            public const string ItemBlueprint = "http://static.moscow.ovh/images/games/rust/icons/blueprintbase.png";
            public const string ItemIconBaseUrl = "http://static.moscow.ovh/images/games/rust/icons/{0}.png";
            public const string MainPanelUrl = "http://static.moscow.ovh/store/gui/rust/store_modal.png";
            public const string ButtonsUrl = "http://static.moscow.ovh/store/gui/rust/store_buttons.png";

            public const string ErrorUrl = "http://static.moscow.ovh/store/gui/rust/store_error.png";
            public const string InfoUrl = "http://static.moscow.ovh/store/gui/rust/store_empty.png";

            private const string ApiUrl = "https://store-api.moscow.ovh/index.php";
            private const string ApiNoSslUrl = "http://store-api.moscow.ovh/index.php";

            private static string _baseUrl = ApiUrl;

            public readonly Dictionary<string, string> InitData = new Dictionary<string, string>()
            {
                ["modules"] = "servers",
                ["action"] = "checkAuth"
            };

            public readonly Dictionary<string, string> GetItemsData = new Dictionary<string, string>()
            {
                ["modules"] = "queue",
                ["action"] = "get"
            };

            public readonly Dictionary<string, string> GiveItemData = new Dictionary<string, string>()
            {
                ["modules"] = "queue",
                ["action"] = "give"
            };

            public readonly Dictionary<string, string> ImagaErrorData = new Dictionary<string, string>()
            {
                ["modules"] = "customError",
                ["action"] = "storeImg"
            };

            public readonly Dictionary<string, string> NoSteamLoginData = new Dictionary<string, string>()
            {
                ["modules"] = "auth",
                ["action"] = "setData"
            };

            public readonly Dictionary<string, string> AutoCommandsData = new Dictionary<string, string>()
            {
                ["modules"] = "queue",
                ["action"] = "autoActivate"
            };

            public readonly Dictionary<string, string> ChangeGlobalDiscount = new Dictionary<string, string>()
            {
                ["modules"] = "config",
                ["action"] = "changeDiscount"
            };

            public readonly Dictionary<string, string> ChangeProductDiscount = new Dictionary<string, string>()
            {
                ["modules"] = "config",
                ["action"] = "changeProductDiscount"
            };

            public readonly Dictionary<string, string> ChangeUserBalance = new Dictionary<string, string>()
            {
                ["modules"] = "users",
                ["action"] = "changeUserBalance",
                ["type"] = "give"
            };

            public readonly Dictionary<string, string> GetUserData = new Dictionary<string, string>()
            {
                ["modules"] = "users",
                ["action"] = "getData"
            };

            public readonly Dictionary<string, string> purchaseUserProduct = new Dictionary<string, string>()
            {
                ["modules"] = "product",
                ["action"] = "purchase"
            };

            public CStoreApi(string storeId, string serverId, string serverKey)
            {
                var dictArray = new[]
                {
                    InitData, GetItemsData, GiveItemData, ImagaErrorData, NoSteamLoginData, AutoCommandsData,
                    ChangeGlobalDiscount, ChangeProductDiscount, ChangeUserBalance, GetUserData, purchaseUserProduct
                };

                foreach (var dict in dictArray)
                {
                    dict["storeID"] = storeId;
                    dict["serverID"] = serverId;
                    dict["serverKey"] = serverKey;
                }
            }

            public static void DisableSsl()
            {
                _baseUrl = ApiNoSslUrl;
            }

            public static void EnableSsl()
            {
                _baseUrl = ApiUrl;
            }

            public static void PostRequest(Dictionary<string, string> data, Action<CJsonResponse> response = null)
            {
                WwwRequests.Request(_baseUrl, data, (res, error) =>
                {
                    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(res))
                    {
                        CLog.LogErrorWithFile($"Ошибка запроса: {error}, Ответ: {res}");
                        response?.Invoke(
                            new CJsonResponse {rawResponse = res, status = "RequestError", message = error});
                        return;
                    }

                    CJsonResponse converted = null;
                    try
                    {
                        var settings = new JsonSerializerSettings
                        {
                            StringEscapeHandling = StringEscapeHandling.EscapeHtml
                        };

                        converted = JsonConvert.DeserializeObject<CJsonResponse>(res, settings);
                    }
                    catch
                    {
                        // ignored
                    }

                    if (converted == null)
                    {
                        CLog.LogErrorWithFile($"Ошибка обработки JSON запроса: {res}");
                        response?.Invoke(new CJsonResponse {rawResponse = res, status = "JsonError", message = res});
                        return;
                    }

                    converted.rawResponse = res;

                    if (converted.isFailure())
                    {
                        if (converted.message == "invalidStoreAuth" || converted.message == "invalidServerAuth")
                        {
                            CLog.LogErrorWithFile(
                                "Ошибка авторизации, убедитесь что вы настроили конфигурацию магазина в файле \"oxide/config/RustStore.json\".\n" +
                                "Данные для авторизации можно найти в магазине во вкладке серверы/установка.");
                            return;
                        }

                        CLog.LogErrorWithFile(converted.message);
                        response?.Invoke(converted);
                        return;
                    }

                    response?.Invoke(converted);
                });
            }
        }

        public class CGuiComponents
        {
            public const string MainPanelName = CPluginInfo.Name + "MainPanel";
            public const string ModalPanelName = CPluginInfo.Name + "ModalPanel";
            public const string ShopButtonName = CPluginInfo.Name + "ShopButton";
            public const string ItemPatternName = CPluginInfo.Name + "ItemPattern";

            public string MainPattern = @"[
                    {       
                        ""parent"": ""Overlay"",        
					    ""name"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.Button"",
                                ""close"": """ + MainPanelName + @""",
                                ""color"": ""0 0 0 0"",
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0 0"",
							    ""anchormax"": ""1 1""
						    },
                            {
                                ""type"":""NeedsCursor""
                            }
					    ]
				    },
                    {
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.RawImage"",
                                ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                ""png"": ""[PNG]"",
                                ""fadeIn"": ""0.4""
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.1 0.1"",
							    ""anchormax"": ""0.9 0.9""
						    }
					    ]
                    },
                    {
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.Button"",
                                ""color"": ""0 0 0 0""
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.1 0.1"",
							    ""anchormax"": ""0.9 0.9""
						    }
					    ]
                    },
                    {
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.Button"",
                                ""close"": """ + MainPanelName + @""",
                                ""color"": ""1 1 1 0""
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.87 0.847"",
							    ""anchormax"": ""0.892 0.883""
						    }
					    ]
                    }]";

            public string InfoPattern = @"[
                    {
                        ""name"": """ + ModalPanelName + @""",
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.RawImage"",
                                ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                ""png"": ""[PNG]"",
                                ""fadeIn"": ""0.4""
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.1 0.1"",
							    ""anchormax"": ""0.9 0.9""
						    }
					    ]
                    },
                    {
                        ""name"": """ + ModalPanelName + @""",
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
					        {
						        ""type"":""UnityEngine.UI.Text"",
						        ""text"":""{0}"",
						        ""fontSize"":48,
						        ""align"": ""MiddleCenter"",
                                ""fadeIn"": ""0.4""
					        },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.1 0.1"",
							    ""anchormax"": ""0.9 0.9""
						    }
		                ]
				    },
                    {
					    ""parent"": """ + MainPanelName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.Button"",
                                ""close"": """ + MainPanelName + @""",
                                ""color"": ""1 1 1 0""
						    },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""0.87 0.847"",
							    ""anchormax"": ""0.892 0.883""
						    }
					    ]
                    }
            ]";

            public string ButtonPattern = @"[
                    {   
                        ""parent"": ""Overlay"",
                        ""name"": """ + ShopButtonName + @""",
					    ""components"":
					    [
						    {
							    ""type"":""UnityEngine.UI.Button"",
                                ""sprite"": ""assets/icons/loot.png"",
                                ""close"": """ + MainPanelName + @""",
                                ""color"": ""1 1 1 0.2"",
                                ""command"": ""store.opencart 0""
                            },
						    {
							    ""type"":""RectTransform"",
							    ""anchormin"": ""{X_POS} {Y_POS}"",
							    ""anchormax"": ""{X_POS_POST} {Y_POS_POST}""
						    }
					    ]
				    }
                ]";

            public string CustomButtonPattern = @"[
                        {   
                            ""parent"": ""Overlay"",
                            ""name"": """ + ShopButtonName + @""",
					        ""components"":
					        [
						        {
							        ""type"":""UnityEngine.UI.Button"",
                                    ""close"": """ + MainPanelName + @""",
                                    ""color"": ""0 0 0 0"",
                                    ""command"": ""store.opencart 0""
                                },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""{X_POS} {Y_POS}"",
							        ""anchormax"": ""{X_POS_POST} {Y_POS_POST}""
						        }
					        ]
				        },
                        {   
                            ""parent"": """ + ShopButtonName + @""",
                            ""name"": ""ShopButtonImage"",
					        ""components"":
					        [
						        {
							        ""type"":""UnityEngine.UI.RawImage"",
                                    ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                    ""png"": ""{PNG}""
						        },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""0 0"",
							        ""anchormax"": ""1 1""
						        }
					        ]
				        }
                    ]";

            public string ErrorPattern = @"[
                        {
                            ""name"": """ + ModalPanelName + @""",
					        ""parent"": """ + MainPanelName + @""",
					        ""components"":
					        [
						        {
							        ""type"":""UnityEngine.UI.RawImage"",
                                    ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                    ""png"": ""[PNG]"",
                                    ""fadeIn"": ""0.4""
						        },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""0.1 0.1"",
							        ""anchormax"": ""0.9 0.9""
						        }
					        ]
                        },
                        {
                            ""name"": """ + ModalPanelName + @""",
					        ""parent"": """ + MainPanelName + @""",
					        ""components"":
					        [
					            {
						            ""type"":""UnityEngine.UI.Text"",
						            ""text"":""{0}"",
						            ""fontSize"":48,
						            ""align"": ""MiddleCenter""
					            },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""0.1 0.1"",
							        ""anchormax"": ""0.9 0.9""
                                }
		                    ]
				        },
                        {
					        ""parent"": """ + MainPanelName + @""",
					        ""components"":
					        [
						        {
							        ""type"":""UnityEngine.UI.Button"",
                                    ""close"": """ + MainPanelName + @""",
                                    ""color"": ""1 1 1 0""
						        },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""0.87 0.847"",
							        ""anchormax"": ""0.892 0.883""
						        }
					        ]
                        }
                    ]";

            public string ItemPatternParent = @"
                        {       
                            ""parent"": """ + MainPanelName + @""",        
					        ""name"": """ + ItemPatternName + @""",
					        ""components"":
					        [
                                {
							        ""type"":""UnityEngine.UI.Image"",
                                    ""color"": ""0 0 0 0""
                                },
						        {
							        ""type"":""RectTransform"",
							        ""anchormin"": ""0 0"",
							        ""anchormax"": ""1 1""
						        }
					        ]
				        },
                    ";

            public string ItemPattern = @"
                                { 
							        ""parent"": """ + ItemPatternName + @""",
							        ""components"":
							        [
								        {
									        ""type"":""UnityEngine.UI.Text"",
                                            ""text"":""{4}"",
									        ""fontSize"":16,
									        ""align"": ""LowerRight"",
                                            ""color"":""1 1 1 0.2"",
                                            ""fadeIN"": ""0.4""
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""{5}"",
							                ""anchormax"": ""{6}""
								        }
							        ]
						        },
						        {
                                    ""name"":""{NAME}"",  
							        ""parent"": """ + ItemPatternName + @""",
							        ""components"":
							        [
								        {
									        ""type"":""UnityEngine.UI.Button"",
                                            ""command"":""store.giveitem {3}"",
                                            ""close"":""" + ItemPatternName + @""",
									        ""color"": ""0 0 0 0"",
                                            ""fadeIN"": ""0.4""
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""{0}"",
							                ""anchormax"": ""{1}""
								        }
							        ]
						        },";

            public string ItemPatternText = @"                            
                                 {
                                    ""name"":""{NAME}.icon"",  
							        ""parent"": ""{NAME}"",
							        ""components"":
							        [
								        {
									        ""type"":""UnityEngine.UI.Text"",
                                            ""text"":""{TEXT}"",
									        ""fontSize"":16,
									        ""align"": ""MiddleCenter"",
                                            ""color"":""1 1 1 0.2"",
                                            ""fadeIN"": ""0.4""
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0 0"",
							                ""anchormax"": ""1 1""
								        }
							        ]
						        },";

            public string ItemPatternBlueprintImage = @"                            
                                 { 
							        ""parent"": ""{NAME}"",
							        ""components"":
							        [
								        {
							                ""type"":""UnityEngine.UI.RawImage"",
                                            ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                            ""png"": ""[PNG]"",
                                            ""fadeIn"": ""0.4""
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0 0"",
							                ""anchormax"": ""1 1""
								        }
							        ]
						        },";

            public string ItemPatternImage = @"                            
                                 { 
                                    ""name"":""{NAME}.icon"",
							        ""parent"": ""{NAME}"",
							        ""components"":
							        [
								        {
							                ""type"":""UnityEngine.UI.RawImage"",
                                            ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                            ""png"": ""{PNG}"",
                                            ""fadeIn"": ""0.4""
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0 0"",
							                ""anchormax"": ""1 1""
								        }
							        ]
						        },";

            public string NextPrevPattern = @"
                                {
							        ""parent"": """ + ItemPatternName + @""",
					                ""components"":
					                [
						                {
							                ""type"":""UnityEngine.UI.RawImage"",
                                            ""sprite"": ""assets/content/textures/generic/fulltransparent.tga"",
                                            ""png"": ""[PNG]"",
                                            ""fadeIn"": ""0.4""
						                },
						                {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.1 0.1"",
							                ""anchormax"": ""0.9 0.9""
						                }
					                ]
                                },
                                {
					                ""parent"": """ + ItemPatternName + @""",
					                ""components"":
					                [
						                {
							                ""type"":""UnityEngine.UI.Button"",
                                            ""color"": ""0 0 0 0""
						                },
						                {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.1 0.1"",
							                ""anchormax"": ""0.9 0.9""
						                }
					                ]
                                },
                                {
					                ""parent"": """ + ItemPatternName + @""",
					                ""components"":
					                [
						                {
							                ""type"":""UnityEngine.UI.Button"",
                                            ""close"": ""MainPanel"",
                                            ""color"": ""1 1 1 0""
						                },
						                {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.87 0.847"",
							                ""anchormax"": ""0.892 0.883""
						                }
					                ]
                                },
                                {  
							        ""parent"": """ + ItemPatternName + @""",
					                ""components"":
					                [
						                {
							                ""type"":""UnityEngine.UI.Button"",
                                            ""close"":""ItemPattern"",
                                            ""command"":""store.giveitemall"",
                                            ""color"": ""1 1 1 0"",
						                },	
						                {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.5125 0.1665"",
							                ""anchormax"": ""0.8625 0.256""
						                }
					                ]
				                },			
                                { 
							        ""parent"": """ + ItemPatternName + @""",
							        ""components"":
							        [
								        {
									        ""type"":""UnityEngine.UI.Button"",
                                            ""close"":""ItemPattern"",
                                            ""command"":""store.refreshcart {0}"",
                                            ""color"": ""1 1 1 0"",
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.5125 0.30"",
							                ""anchormax"": ""0.675 0.389""
								        }
							        ]
						        },
                                {
							        ""parent"": """ + ItemPatternName + @""",
							        ""components"":
							        [
								        {
									        ""type"":""UnityEngine.UI.Button"",
                                            "" "":""" + ItemPatternName + @""",
                                            ""command"":""store.refreshcart {1}"",
                                            ""color"": ""1 1 1 0"",
								        },	
								        {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.7 0.30"",
							                ""anchormax"": ""0.8625 0.389""
								        }
							        ]
						        },
                                {
					                ""parent"": """ + MainPanelName + @""",
					                ""components"":
					                [
						                {
							                ""type"":""UnityEngine.UI.Button"",
                                            ""close"": """ + MainPanelName + @""",
                                            ""color"": ""1 1 1 0""
						                },
						                {
							                ""type"":""RectTransform"",
							                ""anchormin"": ""0.87 0.847"",
							                ""anchormax"": ""0.892 0.883""
						                }
					                ]
                                },";
        }

        public static class CGui
        {
            public static void CreateGlobal(string json)
                => CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(Net.sv.connections), null, "AddUI", json);

            public static void DestroyGlobal(string name)
                => CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(Net.sv.connections), null, "DestroyUI",
                    name);

            public static void Create(Connection con, string text)
                =>
                    CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo {connection = con}, null, "AddUI",
                        text);

            public static void Destroy(Connection con, string name)
                =>
                    CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo {connection = con}, null, "DestroyUI",
                        name);
        }

        public static class CConsoleComands
        {
            public static bool StoreOpen(ConsoleSystem.Arg arg)
            {
                var player = (BasePlayer) arg.Connection?.player;
                if (player == null)
                {
                    return false;
                }

                OpenStore(player);
                return true;
            }

            public static bool StoreRefresh(ConsoleSystem.Arg arg)
            {
                var player = (BasePlayer) arg.Connection?.player;
                if (player == null)
                {
                    return false;
                }

                ShowCart(player, arg.GetInt(0));
                return true;
            }

            public static bool StoreGiveall(ConsoleSystem.Arg arg)
            {
                var player = (BasePlayer) arg.Connection?.player;
                if (player == null)
                {
                    return false;
                }

                SendInfoMessage(player, GetLangMessage(player.UserIDString, "ITEMS.GIVE"));
                GiveAllStoreItems(player);
                return true;
            }

            public static bool StoreGive(ConsoleSystem.Arg arg)
            {
                var player = (BasePlayer) arg.Connection?.player;
                if (player == null)
                {
                    return false;
                }

                GiveStoreItem(player, arg.GetString(0),
                    () => { ShowCart(player, arg.GetInt(1)); },
                    (error) => { SendErrorMessage(player, error); });
                return true;
            }

            public static bool StoreConnect(ConsoleSystem.Arg arg)
            {
                if (!arg.IsAdmin)
                {
                    return false;
                }

                var storeNumber = arg.GetString(0);
                if (string.IsNullOrEmpty(storeNumber))
                {
                    arg.ReplyWith("Не указан номер магазина");
                    return true;
                }

                var serverNumver = arg.GetString(1);
                if (string.IsNullOrEmpty(serverNumver))
                {
                    arg.ReplyWith("Не указан номер сервера");
                    return true;
                }

                var serverKey = arg.GetString(2);
                if (string.IsNullOrEmpty(serverKey))
                {
                    arg.ReplyWith("Не указан ключ сервера");
                    return true;
                }

                var config = StoreConfig;

                config.StoreId = storeNumber;
                config.ServerId = serverNumver;
                config.ApiKey = serverKey;
                config.Save();

                Interface.Oxide.ReloadPlugin(CPluginInfo.Name);

                arg.ReplyWith("Конфиг магазина успешно изменен.");
                return true;
            }
        }

        private const float UberIdentificator = 9831023;

        public static readonly Lang Lang = GetLibrary<Lang>();
        private static readonly Oxide.Core.Libraries.Timer Timer = GetLibrary<Oxide.Core.Libraries.Timer>();
        public static RustStore Instance;
        public static CStoreApi StoreApi;
        private static Dictionary<ulong, CStoreCache> _storeCaches;
        private static CGuiComponents _guiComponents;
        public static CWwwRequests WwwRequests;
        private static CQueueImageCache _imageCache;

        internal static CStoreConfig StoreConfig;
        private static readonly Dictionary<string, string> ShortnameToDisplayName = new Dictionary<string, string>();

        private readonly Dictionary<string, UberItem> _uberItems = new Dictionary<string, UberItem>()
        {
            ["uberhatchet"] = new UberItem("hatchet", 815040374),
            ["uberpickaxe"] = new UberItem("pickaxe", 837760607),
            ["ubericepick"] = new UberItem("icepick.salvaged", 844666224)
        };

        private readonly HashSet<BasePlayer> _storePlayers = new HashSet<BasePlayer>();

        public RustStore()
        {
            HasConfig = true;
            Instance = this;
            _storeCaches = new Dictionary<ulong, CStoreCache>();
            _guiComponents = new CGuiComponents();
        }


        private static void CompatibilityCheck()
        {
            try
            {
                var itemDef = ItemManager.FindItemDefinition("explosives");

                var testItem = ItemManager.Create(itemDef, 10);
                if (testItem == null)
                {
                    throw new Exception("ItemManager.Create EXCEPTION");
                }

                testItem.Remove();

                ConsoleSystem.Run(ConsoleSystem.Option.Server, "server.fps");
            }
            catch (Exception ex)
            {
                CLog.Error($"Что-то не так с функциями выдачи предметов, обратитесь в тех. поддержку.\nОшибка:{ex}");

                Interface.Oxide.UnloadPlugin(CPluginInfo.Name);
            }
        }

        private void DownloadMainGuiImages()
        {
            if (!string.IsNullOrEmpty(StoreConfig.CustomStoreIcon))
            {
                _imageCache.CacheImage(StoreConfig.CustomStoreIcon, id =>
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        CLog.Error(
                            "Ошибка при скачивании иконки магазина из конфига, будет установлена стандартная иконка.");
                        return;
                    }

                    _guiComponents.ButtonPattern = _guiComponents.CustomButtonPattern.Replace("{PNG}", id);
                });
            }

            _imageCache.CacheImage(
                StoreConfig.UseEnglishImages ? CStoreApi.MainPanelUrl.Replace(".png", "_en.png") : CStoreApi.MainPanelUrl,
                id =>
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        CLog.Error("Ошибка при скачивании GUI корзины 'MainPanel', обратитесь в тех. поддержку.");
                        Interface.Oxide.UnloadPlugin(CPluginInfo.Name);
                        return;
                    }

                    _guiComponents.MainPattern = _guiComponents.MainPattern.Replace("[PNG]", id);
                });

            _imageCache.CacheImage(CStoreApi.InfoUrl, id =>
            {
                if (string.IsNullOrEmpty(id))
                {
                    CLog.Error("Ошибка при скачивании GUI корзины 'InfoPanel', обратитесь в тех. поддержку.");
                    Interface.Oxide.UnloadPlugin(CPluginInfo.Name);
                    return;
                }

                _guiComponents.InfoPattern = _guiComponents.InfoPattern.Replace("[PNG]", id);
            });

            _imageCache.CacheImage(CStoreApi.ErrorUrl, id =>
            {
                if (string.IsNullOrEmpty(id))
                {
                    CLog.Error("Ошибка при скачивании GUI корзины 'ErrorPanel', обратитесь в тех. поддержку.");
                    Interface.Oxide.UnloadPlugin(CPluginInfo.Name);
                    return;
                }

                _guiComponents.ErrorPattern = _guiComponents.ErrorPattern.Replace("[PNG]", id);
            });

            _imageCache.CacheImage(CStoreApi.ItemBlueprint, id =>
            {
                if (string.IsNullOrEmpty(id))
                {
                    CLog.Error("Ошибка при скачивании GUI корзины 'BlueprintImage', обратитесь в тех. поддержку.");
                    return;
                }

                _guiComponents.ItemPatternBlueprintImage =
                    _guiComponents.ItemPatternBlueprintImage.Replace("[PNG]", id);
            });

            _imageCache.CacheImage(
                StoreConfig.UseEnglishImages ? CStoreApi.ButtonsUrl.Replace(".png", "_en.png") : CStoreApi.ButtonsUrl,
                id =>
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        CLog.Error("Ошибка при скачивании GUI корзины 'ButtonsPanel', обратитесь в тех. поддержку.");
                        Interface.Oxide.UnloadPlugin(CPluginInfo.Name);
                        return;
                    }

                    _guiComponents.ButtonPattern = _guiComponents.ButtonPattern
                        .Replace("{X_POS}", StoreConfig.IconPosX.ToString(CultureInfo.InvariantCulture))
                        .Replace("{X_POS_POST}",
                            (StoreConfig.IconPosX + StoreConfig.IconWidth).ToString(CultureInfo.InvariantCulture))
                        .Replace("{Y_POS}",
                            (1 - StoreConfig.IconPosY - StoreConfig.IconHeight).ToString(CultureInfo.InvariantCulture))
                        .Replace("{Y_POS_POST}", (1 - StoreConfig.IconPosY).ToString(CultureInfo.InvariantCulture));

                    _guiComponents.NextPrevPattern = _guiComponents.NextPrevPattern.Replace("[PNG]", id);

                    var commandLib = GetLibrary<Command>();
                    commandLib.AddChatCommand("store", this, Storecmd);
                    commandLib.AddConsoleCommand("store.giveitem", this, CConsoleComands.StoreGive);
                    commandLib.AddConsoleCommand("store.opencart", this, CConsoleComands.StoreOpen);
                    commandLib.AddConsoleCommand("store.refreshcart", this, CConsoleComands.StoreRefresh);
                    commandLib.AddConsoleCommand("store.giveitemall", this, CConsoleComands.StoreGiveall);

                    _imageCache.MaxActiveDownloads = 8;

                    foreach (var hookName in Hooks.Keys)
                    {
                        Subscribe(hookName);
                    }

                    foreach (var ply in BasePlayer.activePlayerList)
                    {
                        OnPlayerSleepEnded(ply);
                    }

                    Timer.Repeat(60 * 5, 0, AutoCommandsGive, this);

                    CLog.Debug("GUI компоненты успешно загружены.\n" +
                               "Магазин полностью инициализирован.");
                });
        }

        [HookMethod("OnServerInitialized")]
        private void OnServerInitialized()
        {
            CLog.Debug("Инициализация магазина...");

            foreach (var hookName in Hooks.Keys)
            {
                if (hookName == nameof(OnServerInitialized))
                {
                    continue;
                }

                Unsubscribe(hookName);
            }

            GetLibrary<Command>().AddConsoleCommand("store.connect", this, CConsoleComands.StoreConnect);

            WwwRequests = new CWwwRequests();
            _imageCache = new CQueueImageCache()
            {
                MaxActiveDownloads = 1
            };

            // Tests for plugin compatibility with last game verison
            CompatibilityCheck();

            // Parse plugin's config or create new
            StoreConfig = CStoreConfig.Parse(Config);

            // Initialize plugin's lang
            StoreConfig.InitializeMessages();

            // Initialize store api
            if (StoreConfig.EnableSsl)
            {
                CStoreApi.EnableSsl();
            }
            else
            {
                CStoreApi.DisableSsl();
            }

            StoreApi = new CStoreApi(StoreConfig.StoreId, StoreConfig.ServerId, StoreConfig.ApiKey);
            CStoreApi.PostRequest(StoreApi.InitData, r =>
            {
                if (r.isFailure())
                {
                    return;
                }

                CLog.Debug("Загрузка GUI компонентов.");
                DownloadMainGuiImages();
            });

            ShortnameToDisplayName.Clear();
            foreach (var item in ItemManager.itemList)
            {
                ShortnameToDisplayName[item.shortname] = item.displayName.translated;
            }
        }

        private void Storecmd(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                OpenStore(player);
                return;
            }

            if (args[0] == "login")
            {
                var steam = player.UserIDString;

                if (args.Length != 2)
                {
                    player.ChatMessage(GetLangMessage(steam, "STORE.NOSTEAM.SYNTAX"));
                    return;
                }

                var data = StoreApi.NoSteamLoginData;
                data["token"] = args[1];
                data["steamID"] = steam;

                CStoreApi.PostRequest(data, resp =>
                {
                    if (resp.isFailure())
                    {
                        string messageKey;
                        switch (resp.message)
                        {
                            case "invalidToken":
                                messageKey = "STORE.NOSTEAM.TOKEN.INVALID";
                                break;
                            case "expiredToken":
                                messageKey = "STORE.NOSTEAM.TOKEN.EXPIRED";
                                break;
                            case "userAlreadyRegistered":
                                messageKey = "STORE.NOSTEAM.REGISTERED";
                                break;
                            default:
                                messageKey = "STORE.ERROR";
                                break;
                        }

                        player.ChatMessage(GetLangMessage(steam, messageKey));
                        return;
                    }

                    CLog.File(
                        $"NoSteam игрок {player.displayName} ({player.UserIDString}) привязан к аккаунту {resp.data.ToString()}");
                    player.ChatMessage(GetLangMessage(steam, "STORE.NOSTEAM.SUCCESS"));
                });
            }
            else if (args.Length >= 2 && args[0] == "give" && args[1] == "all")
            {
                CStoreCache storeCache;
                if (_storeCaches.TryGetValue(player.userID, out storeCache) && storeCache.IsGivingAll())
                {
                    player.ChatMessage(GetLangMessage(player.UserIDString, "ITEMS.GIVE"));
                    return;
                }

                GetItems(player, () => GiveAllStoreItems(player), (error) => SendErrorMessage(player, error));
            }
        }

        [HookMethod("OnPlayerSleepEnded")]
        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (_storePlayers.Contains(player))
            {
                return;
            }

            var c = player.net?.connection;
            if (c == null)
            {
                return;
            }

            if (StoreConfig.ShowIcon)
            {
                CGui.Create(c, _guiComponents.ButtonPattern);
            }

            _storePlayers.Add(player);
        }

        [HookMethod("OnItemRepair")]
        private object OnItemRepair(BasePlayer player, Item item)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (item.busyTime == UberIdentificator)
            {
                return false;
            }

            return null;
        }

        [HookMethod("OnDispenserBonus")]
        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            var activeItem = player.GetActiveItem();
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (activeItem == null || activeItem.busyTime != UberIdentificator)
            {
                return;
            }

            ItemDefinition itemDef;
            int amount;
            var cookable = item.info.itemMods.FirstOrDefault(m => m is ItemModCookable) as ItemModCookable;
            if (cookable == null || cookable.becomeOnCooked == null)
            {
                var burnable = item.info.itemMods.FirstOrDefault(m => m is ItemModBurnable) as ItemModBurnable;
                if (burnable == null || burnable.byproductItem == null)
                {
                    return;
                }

                itemDef = burnable.byproductItem;
                amount = burnable.byproductAmount;
            }
            else
            {
                itemDef = cookable.becomeOnCooked;
                amount = cookable.amountOfBecome;
            }

            item.info = itemDef;
            item.amount *= amount;
        }

        [HookMethod("OnDispenserGather")]
        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity.ToPlayer();
            if (player == null)
            {
                return;
            }

            OnDispenserBonus(dispenser, player, item);
        }

        [HookMethod("OnPlayerDisconnected")]
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _storePlayers.Remove(player);
        }

        private static string FormatString(int str, string first, string second, string third)
        {
            var formatted = str + " ";
            if (str > 100)
            {
                str = str % 100;
            }

            if (str > 9 && str < 21)
            {
                formatted += third;
            }
            else
            {
                switch (str % 10)
                {
                    case 1:
                        formatted += first;
                        break;
                    case 2:
                    case 3:
                    case 4:
                        formatted += second;
                        break;
                    default:
                        formatted += third;
                        break;
                }
            }

            return formatted;
        }

        public static string GetLangMessage(string userId, string key) => Lang.GetMessage(key, Instance, userId);

        private static void GetItems(BasePlayer player, Action success, Action<string> onFail)
        {
            var userId = player.UserIDString;
            var data = StoreApi.GetItemsData;
            data["steamID"] = userId;
            CStoreApi.PostRequest(data, resp =>
            {
                if (resp.isFailure())
                {
                    onFail(GetLangMessage(userId, "STORE.ERROR"));
                    return;
                }

                CItemJson[] converted;
                try
                {
                    converted = resp.data.ToObject<CItemJson[]>();
                }
                catch (Exception ex)
                {
                    CLog.LogErrorWithFile(
                        $"Ошибка при получении корзины (JSON). Игрок: {player.UserIDString}, Ответ: {resp.rawResponse}, Ошибка: {ex}");
                    onFail(GetLangMessage(userId, "STORE.ERROR"));
                    return;
                }

                CStoreCache storeCache;
                if (!_storeCaches.TryGetValue(player.userID, out storeCache))
                {
                    storeCache = _storeCaches[player.userID] = new CStoreCache();
                }

                var playerCart = storeCache.CachedCart;
                var itemsToRemove = playerCart.Where(f => f.Value.CanBeRemoved()).Select(pk => pk.Key).ToArray();
                for (var i = itemsToRemove.Length - 1; i >= 0; i--)
                {
                    playerCart.Remove(itemsToRemove[i]);
                }

                foreach (var item in converted)
                {
                    if (playerCart.ContainsKey(item.queueID))
                    {
                        continue;
                    }

                    playerCart.Add(item.queueID, item);
                }

                success();
            });
        }

        private static void RequestItemGive(string userId, CRawStoreItem itemJson, Action giveAction,
            Action<string> onFail = null)
        {
            itemJson.MarkGived();
            var queueId = itemJson.queueID;
            var data = StoreApi.GiveItemData;
            data["steamID"] = userId;
            data["queueID"] = queueId;
            CLog.File($"Запрос выдачи (номер покупки {itemJson.pid}, номер выдачи {queueId}) игроку {userId}", "give");
            CStoreApi.PostRequest(data, res =>
            {
                if (res.data == null)
                {
                    CLog.LogErrorWithFile("Ошибка выдачи (data == null) " +
                                          $"(номер покупки {itemJson.pid}, номер выдачи {queueId}, игрок {userId}). " +
                                          $"Ответ: {res.rawResponse}");
                    return;
                }

                var storeSteam = res.data.ToString();
                var plyLog = storeSteam == userId ? userId : $"{storeSteam} (nosteam: {userId})";

                CLog.File($"Выдача (номер покупки {itemJson.pid}, номер выдачи {queueId}) игроку {plyLog}", "give");

                if (!res.isSuccess())
                {
                    CLog.File(
                        $"Запрет выдачи (номер покупки {itemJson.pid}, номер выдачи {queueId}) игроку {plyLog}, message: {res.message}",
                        "give");
                    onFail?.Invoke(GetLangMessage(userId, "STORE.ERROR"));
                    return;
                }

                giveAction();
                itemJson.MarkGived();
            });
        }

        private static void AutoCommandsGive()
        {
            CStoreApi.PostRequest(StoreApi.AutoCommandsData, res =>
            {
                if (res.isFailure())
                {
                    return;
                }

                CAutoItemJson[] converted;
                try
                {
                    converted = res.data.ToObject<CAutoItemJson[]>();
                }
                catch (Exception ex)
                {
                    CLog.LogErrorWithFile(
                        $"Ошибка при получении автоматической корзины (JSON). Ответ: {res.rawResponse}, Ошибка: {ex}");
                    return;
                }

                if (converted.Length == 0)
                {
                    return;
                }

                CLog.Debug("Обработка автоматической корзины.");

                foreach (var autoItem in converted)
                {
                    var userId = autoItem.steamID;
                    RequestItemGive(userId, autoItem, () =>
                    {
                        foreach (var cmd in autoItem.data)
                        {
                            ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd.Replace("{steamid}", userId));
                        }
                    });
                }
            });
        }

        public static void GiveStoreItem(BasePlayer player, string queueId, Action onGived, Action<string> onFail)
        {
            CStoreCache storeCache;
            CItemJson itemJson;
            if (!_storeCaches.TryGetValue(player.userID, out storeCache) ||
                !storeCache.CachedCart.TryGetValue(queueId, out itemJson))
            {
                onFail(GetLangMessage(player.UserIDString, "STORE.ERROR"));
                return;
            }

            GiveStoreItem(player, itemJson, onGived, onFail);
        }

        private static void GiveStoreItem(BasePlayer player, CItemJson itemJson, Action onGived, Action<string> onFail)
        {
            var userId = player.UserIDString;

            if (itemJson.isGived())
            {
                onFail(GetLangMessage(userId, "ITEM.ALREADYGIVED"));
                return;
            }

            // Check that player can get items
            if (player.IsDead() || player.IsWounded() || player.IsSleeping() || player.IsSpectating())
            {
                onFail(GetLangMessage(userId, "GIVE.MIND"));
                return;
            }

            if (StoreConfig.BanOtherCupboardGive || StoreConfig.OnlyCupboardGive)
            {
                if (player.IsBuildingBlocked())
                {
                    onFail(GetLangMessage(userId, "GIVE.CUPBOARD.OTHER"));
                    return;
                }

                if (StoreConfig.OnlyCupboardGive && !player.IsBuildingAuthed())
                {
                    onFail(GetLangMessage(userId, "GIVE.CUPBOARD.OWNER"));
                    return;
                }
            }

            var queueId = itemJson.queueID;

            // Change give method by type
            switch (itemJson.type)
            {
                case EItemType.Blueprint:
                case EItemType.GameItem:
                    var gameItem = itemJson.data.ToObject<CGameItemJson>();
                    var isBp = itemJson.type == EItemType.Blueprint;

                    if (gameItem.block)
                    {
                        onFail(GetLangMessage(userId, "ITEM.BLOCKED"));
                        return;
                    }

                    var shortname = gameItem.itemName;
                    var amount = gameItem.quantity;

                    var obj = Interface.Oxide.CallHook("CanGiveItemStore", player, shortname, amount, isBp);
                    if (obj != null)
                    {
                        CLog.Debug(
                            $"Выдача предмета заблокирована сторонним плагином. Игрок: {player.UserIDString}, номер покупки {itemJson.pid}, номер выдачи {queueId}");
                        var str = obj as string;
                        if (!string.IsNullOrEmpty(str))
                        {
                            onFail(str);
                        }

                        return;
                    }

                    ulong skin = 0;
                    var isUberItem = false;
                    UberItem uberItem;
                    if (Instance._uberItems.TryGetValue(shortname, out uberItem))
                    {
                        shortname = uberItem.Shortname;
                        skin = uberItem.SkinId;
                        isUberItem = true;
                    }

                    var itemDef = ItemManager.FindItemDefinition(shortname);
                    if (itemDef == null)
                    {
                        onFail(GetLangMessage(userId, "STORE.ERROR"));
                        return;
                    }

                    var inv = player.inventory;
                    var stack = 1;
                    var capacity = inv.containerMain.capacity + inv.containerBelt.capacity;
                    var containSlots = 0;
                    var needSplit = false;

                    if (!StoreConfig.StackItemsInOneSLot)
                    {
                        if (itemDef.stackable > 1)
                        {
                            stack = itemDef.stackable;
                        }

                        containSlots = (int) Math.Ceiling((double) amount / stack);
                        needSplit = containSlots < capacity;
                    }

                    var needSlots = inv.containerMain.itemList.Count + inv.containerBelt.itemList.Count +
                                    (needSplit ? containSlots : 1) - capacity;

                    if (needSlots > 0)
                    {
                        onFail(new StringBuilder(GetLangMessage(userId, "SLOTS.RELEASE"))
                            .Replace("{SLOTS}", FormatString(needSlots,
                                GetLangMessage(userId, "SLOTS.DECLENSION1"),
                                GetLangMessage(userId, "SLOTS.DECLENSION2"),
                                GetLangMessage(userId, "SLOTS.DECLENSION3")))
                            .Replace("{ITEM.NAME}", itemDef.displayName.translated + (isBp ? " BP" : ""))
                            .Replace("{AMOUNT}", amount.ToString())
                            .ToString());
                        return;
                    }

                    RequestItemGive(userId, itemJson, () =>
                    {
                        Action<int> giveItemFunc = (giveAmount) =>
                        {
                            if (isBp)
                            {
                                var bp = ItemManager.Create(ResearchTable.GetBlueprintTemplate());
                                bp.blueprintTarget = itemDef.itemid;
                                player.GiveItem(bp);
                                return;
                            }

                            var giveItem = ItemManager.Create(itemDef, giveAmount, skin);
                            player.GiveItem(giveItem);
                            if (giveItem.info.shortname == "smallwaterbottle" && giveItem.contents != null)
                            {
                                var water = ItemManager.CreateByName("water", 250);
                                water?.MoveToContainer(giveItem.contents);
                            }

                            if (!isUberItem)
                            {
                                return;
                            }

                            var maxCond = giveItem.info.condition.max;
                            giveItem.info.condition.max = float.MaxValue;
                            giveItem.busyTime = UberIdentificator;
                            giveItem.maxCondition *= StoreConfig.UberModifier;
                            giveItem.condition *= StoreConfig.UberModifier;
                            giveItem.SetFlag(global::Item.Flag.OnFire, true);
                            giveItem.MarkDirty();
                            giveItem.info.condition.max = maxCond;
                        };

                        if (needSplit)
                        {
                            for (var i = amount; i > 0; i -= stack)
                            {
                                giveItemFunc(i > stack ? stack : i);
                            }
                        }
                        else
                        {
                            giveItemFunc(amount);
                        }

                        onGived();
                    }, onFail);
                    return;
                case EItemType.Commands:
                    var cmds = itemJson.data.ToObject<List<string>>();
                    var canCommand = Interface.Oxide.CallHook("CanExecuteCommandsStore", player, cmds);
                    if (canCommand != null)
                    {
                        CLog.Debug($"Выдача услуги заблокирована сторонним плагином. Игрок: {player.UserIDString}," +
                                   $" номер покупки {itemJson.pid}, номер выдачи {queueId}");
                        var str = canCommand as string;
                        if (!string.IsNullOrEmpty(str))
                        {
                            onFail(str);
                        }

                        return;
                    }

                    RequestItemGive(userId, itemJson, () =>
                    {
                        foreach (var cmd in cmds)
                        {
                            ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd.Replace("{steamid}", userId)
                                .Replace("{playerName}", player.displayName));
                        }

                        onGived();
                    }, onFail);
                    return;
            }

            onFail(GetLangMessage(userId, "STORE.ERROR"));
        }

        public static void GiveAllStoreItems(BasePlayer player)
        {
            CStoreCache storeCache;
            if (!_storeCaches.TryGetValue(player.userID, out storeCache))
            {
                return;
            }

            storeCache.MarkGivingAll();

            var storeItem = storeCache.CachedCart.Values.FirstOrDefault((item) => !item.isGived());
            if (storeItem == null)
            {
                storeCache.MarkGivedAll();
                CGui.Destroy(player.net.connection, CGuiComponents.ModalPanelName);
                SendInfoMessage(player, GetLangMessage(player.UserIDString, "ITEMS.GIVEN"));
                return;
            }

            GiveStoreItem(player, storeItem,
                () => { GiveAllStoreItems(player); },
                (error) =>
                {
                    storeCache.MarkGivedAll();
                    CGui.Destroy(player.net.connection, CGuiComponents.ModalPanelName);
                    SendErrorMessage(player, error);
                });
        }

        public static void ShowCart(BasePlayer player, int page)
        {
            CStoreCache storeCache;
            if (!_storeCaches.TryGetValue(player.userID, out storeCache))
            {
                return;
            }

            if (page < 0)
            {
                SendInfoMessage(player, GetLangMessage(player.UserIDString, "ITEMS.GIVEN"));
                return;
            }

            var connection = player.net.connection;
            var playerItems = storeCache.CachedCart.Values.Where((item) => !item.isGived()).ToArray();
            var playerItemsCount = playerItems.Length;
            if (playerItemsCount == 0)
            {
                SendInfoMessage(player, GetLangMessage(player.UserIDString, "ITEMS.EMPTY"));
                return;
            }

            CGui.Destroy(connection, CGuiComponents.ItemPatternName);

            var jsonBuilder = new StringBuilder("[", _guiComponents.ItemPattern.Length * 5)
                .Append(_guiComponents.ItemPatternParent);

            var topMin = 0.656f;
            var topMax = 0.834f;
            var leftMin = 0.1375f;
            var leftMax = 0.2375f;

            var firsItemIndex = page * 15;
            var nextPage = page;
            var prevPage = page > 1 ? page - 1 : 0;

            var max = firsItemIndex + 15;
            if (max >= playerItemsCount)
            {
                max = playerItemsCount;
            }
            else
            {
                nextPage++;
            }

            jsonBuilder.Append(_guiComponents.NextPrevPattern)
                .Replace("{0}", prevPage.ToString())
                .Replace("{1}", nextPage.ToString());

            if (playerItemsCount - firsItemIndex == 1)
            {
                page--;
            }

            var langMissed = GetLangMessage(player.UserIDString, "IMAGE.MISSED");
            var langLoading = GetLangMessage(player.UserIDString, "IMAGE.LOADING");

            for (var i = firsItemIndex; i < max; i++)
            {
                var info = playerItems[i];
                var itemName = "store.item." + info.queueID;
                CGui.Destroy(connection, itemName);

                var amount = 1;
                var iconUrl = info.icon;
                var imageMissed = langMissed;

                if (info.type == EItemType.GameItem || info.type == EItemType.Blueprint)
                {
                    var gameItem = info.data.ToObject<CGameItemJson>();
                    iconUrl = string.Format(CStoreApi.ItemIconBaseUrl, gameItem.itemName);
                    string displayName;
                    if (ShortnameToDisplayName.TryGetValue(gameItem.itemName, out displayName))
                    {
                        imageMissed = displayName;
                    }

                    amount = gameItem.quantity;
                }

                jsonBuilder.Append(_guiComponents.ItemPattern)
                    .Replace("{0}", $"{leftMin} {topMin}")
                    .Replace("{1}", $"{leftMax} {topMax}")
                    .Replace("{3}", $"{info.queueID} {page}")
                    .Replace("{4}", amount == 1 ? "" : amount.ToString()) // AMOUNT
                    .Replace("{5}", $"{leftMin} {topMin}")
                    .Replace("{6}", $"{leftMax - 0.003f}5 {topMax + 0.015f}");

                if (info.type == EItemType.Blueprint)
                {
                    jsonBuilder.Append(_guiComponents.ItemPatternBlueprintImage);
                }

                string cachedImageId;
                if (_imageCache.GetCachedImage(iconUrl, out cachedImageId))
                {
                    jsonBuilder.Append(_guiComponents.ItemPatternImage)
                        .Replace("{PNG}", cachedImageId);
                }
                else
                {
                    jsonBuilder.Append(_guiComponents.ItemPatternText)
                        .Replace("{TEXT}", langLoading);

                    _imageCache.CacheImage(iconUrl, (imageId) =>
                    {
                        CGui.Destroy(connection, itemName + ".icon");

                        var iconBuilder = new StringBuilder("[");

                        if (string.IsNullOrEmpty(imageId))
                        {
                            CLog.Error($"Ошибка при скачивании картинки для предмета #{info.queueID}");
                            iconBuilder.Append(_guiComponents.ItemPatternText)
                                .Replace("{TEXT}", imageMissed);
                        }
                        else
                        {
                            iconBuilder.Append(_guiComponents.ItemPatternImage)
                                .Replace("{NAME}", itemName)
                                .Replace("{PNG}", imageId);
                        }

                        CGui.Create(connection, iconBuilder.Append("]").ToString());
                    });
                }

                jsonBuilder.Replace("{NAME}", itemName);

                leftMin += 0.125f;
                if (leftMin <= 0.8625f)
                {
                    leftMax += 0.125f;
                    continue;
                }

                topMin -= 0.2225f;
                if (topMin < 0.15f)
                {
                    break;
                }

                leftMin = 0.1375f;
                leftMax = 0.2375f;
                topMax -= 0.2225f;
            }

            CGui.Create(connection, jsonBuilder.Append("]").ToString());
        }

        public static void OpenStore(BasePlayer player)
        {
            var connection = player.net.connection;
            CGui.Destroy(connection, CGuiComponents.MainPanelName);
            CGui.Create(connection, _guiComponents.MainPattern);

            CStoreCache storeCache;
            if (_storeCaches.TryGetValue(player.userID, out storeCache) && storeCache.IsGivingAll())
            {
                SendInfoMessage(player, GetLangMessage(player.UserIDString, "ITEMS.GIVE"));
                return;
            }

            GetItems(player,
                () => { ShowCart(player, 0); },
                (error) => { SendErrorMessage(player, error); });
        }

        public static void SendInfoMessage(BasePlayer player, string message)
        {
            //player.ChatMessage(message);
            CGui.Create(player.net.connection, _guiComponents.InfoPattern.Replace("{0}", message));
        }

        public static void SendErrorMessage(BasePlayer player, string message)
        {
            //player.ChatMessage(message);
            CGui.Create(player.net.connection, _guiComponents.ErrorPattern.Replace("{0}", message));
        }

        public override void HandleRemovedFromManager(PluginManager manager)
        {
            foreach (var ply in _storePlayers)
            {
                var connection = ply?.net?.connection;
                if (connection == null)
                {
                    continue;
                }

                CGui.Destroy(connection, CGuiComponents.MainPanelName);
                CGui.Destroy(connection, CGuiComponents.ShopButtonName);
            }

            WwwRequests?.Dispose();
            _imageCache?.Dispose();
            base.HandleRemovedFromManager(manager);
        }

        [HookMethod("APIIsInitialized")]
        public bool APIIsInitialized() => _imageCache != null && _imageCache.MaxActiveDownloads > 1;

        [HookMethod("APIChangeGlobalDiscount")]
        // ReSharper disable once InconsistentNaming
        public bool APIChangeGlobalDiscount(int discount, Action<string> callback)
        {
            if (discount < 0 || discount > 99)
            {
                callback?.Invoke("ERROR.DISCOUNT");
                return false;
            }

            var data = StoreApi.ChangeGlobalDiscount;
            data["discount"] = discount.ToString();

            CStoreApi.PostRequest(data, resp => { callback?.Invoke(resp.isSuccess() ? "SUCCESS" : "ERROR"); });

            return true;
        }

        [HookMethod("APIChangeProductDiscount")]
        // ReSharper disable once InconsistentNaming
        public bool APIChangeProductDiscount(int discount, int productId, Action<string> callback)
        {
            if (discount < 0 || discount > 99)
            {
                callback?.Invoke("ERROR.DISCOUNT");
                return false;
            }

            var data = StoreApi.ChangeProductDiscount;
            data["discount"] = discount.ToString();
            data["productID"] = productId.ToString();

            CStoreApi.PostRequest(data, resp => { callback?.Invoke(resp.isSuccess() ? "SUCCESS" : "ERROR"); });

            return true;
        }

        [HookMethod("APIChangeUserBalance")]
        // ReSharper disable once InconsistentNaming
        public bool APIChangeUserBalance(ulong steam, int balanceChange, Action<string> callback)
        {
            var data = StoreApi.ChangeUserBalance;
            data["steamID"] = steam.ToString();
            data["sum"] = balanceChange.ToString();

            CStoreApi.PostRequest(data, resp => { callback?.Invoke(resp.isSuccess() ? "SUCCESS" : "ERROR"); });

            return true;
        }

        [HookMethod("APIPurchaseUserProduct")]
        // ReSharper disable once InconsistentNaming
        public bool APIPurchaseUserProduct(ulong steam, int productID, int quantity, string productName,
            int productPrice, Action<bool, string, int, float, float> callback)
        {
            var data = StoreApi.purchaseUserProduct;
            data["steamID"] = steam.ToString();
            data["productID"] = productID.ToString();
            data["quantity"] = quantity.ToString();
            data["productPrice"] = productPrice.ToString();
            data["productName"] = productName.ToString();

            CStoreApi.PostRequest(data, resp =>
            {
                var userData = resp.data?.ToObject<List<float>>();
                if (userData == null || userData.Count < 3)
                {
                    CLog.LogErrorWithFile(
                        $"Ошибка при исполнении API PurchaseUserProduct (некорректный ответ JSON, обратитесь в поддержку). Ответ: {resp.rawResponse}");
                    callback?.Invoke(false, "fatalError", 0, 0, 0);
                    return;
                }

                callback?.Invoke(resp.isSuccess(), resp.message, (int) userData[0], (float) userData[1],
                    (float) userData[2]);
            });
            return true;
        }

        [HookMethod("APIGetUserData")]
        // ReSharper disable once InconsistentNaming
        public bool APIGetUserData(ulong steam, Action<string, Dictionary<string, object>> callback)
        {
            if (callback == null)
            {
                return false;
            }

            var data = StoreApi.GetUserData;
            data["steamID"] = steam.ToString();

            CStoreApi.PostRequest(data, resp =>
            {
                if (resp.isFailure())
                {
                    callback("ERROR", null);
                    return;
                }

                Dictionary<string, object> converted;

                try
                {
                    converted = resp.data.ToObject<Dictionary<string, object>>();
                }
                catch (Exception ex)
                {
                    CLog.LogErrorWithFile(
                        $"Ошибка при исполнении API GetUserData (JSON). Ответ: {resp.rawResponse}, Ошибка: {ex}");
                    callback("ERROR.JSON", null);
                    return;
                }

                callback("SUCCESS", converted);
            });

            return true;
        }

        protected override void LoadDefaultConfig()
        {
        }

        protected override void LoadDefaultMessages()
        {
        }
    }
}