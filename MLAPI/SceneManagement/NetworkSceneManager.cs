using System.Collections.Generic;
using System;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Exceptions;
using MLAPI.Internal;
using MLAPI.Logging;
using MLAPI.Messaging;
using MLAPI.Security;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MLAPI.SceneManagement
{
    /// <summary>
    /// Main class for managing network scenes
    /// </summary>
    public static class NetworkSceneManager
    {
        /// <summary>
        /// Delegate for when the scene has been switched
        /// </summary>
        public delegate void SceneSwitchedDelegate();
        /// <summary>
        /// Event that is invoked when the scene is switched
        /// </summary>
        public static event SceneSwitchedDelegate OnSceneSwitched;

        public static bool useCustomHandler = false;
        public static uint customSceneIndex = 0;

        public delegate void CustomHandlerLoadSceneDelegate(Action sceneLoadingFinished);
        public static event CustomHandlerLoadSceneDelegate onCustomHandlerLoadScene;

        public delegate bool CustomHandlerIsSceneSwitchAllowedDelegate();
        public static event CustomHandlerIsSceneSwitchAllowedDelegate onCustomHandlerIsSceneSwitchAllowed;

        public delegate bool CustomHandlerHasSceneMismatchDelegate();
        public static event CustomHandlerHasSceneMismatchDelegate onCustomHandlerHasSceneMismatch;

        internal static readonly HashSet<string> registeredSceneNames = new HashSet<string>();
        internal static readonly Dictionary<string, uint> sceneNameToIndex = new Dictionary<string, uint>();
        internal static readonly Dictionary<uint, string> sceneIndexToString = new Dictionary<uint, string>();
        internal static readonly Dictionary<Guid, SceneSwitchProgress> sceneSwitchProgresses = new Dictionary<Guid, SceneSwitchProgress>();
        private static Scene lastScene;
        private static string nextSceneName;
        private static bool isSwitching = false;
        internal static uint currentSceneIndex = 0;
        internal static Guid currentSceneSwitchProgressGuid = new Guid();
        internal static bool isSpawnedObjectsPendingInDontDestroyOnLoad = false;

        internal static void SetCurrentSceneIndex()
        {
            if (!sceneNameToIndex.ContainsKey(SceneManager.GetActiveScene().name))
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("The current scene (" + SceneManager.GetActiveScene().name + ") is not regisered as a network scene.");
                return;
            }
            currentSceneIndex = sceneNameToIndex[SceneManager.GetActiveScene().name];
            CurrentActiveSceneIndex = currentSceneIndex;
        }

        internal static uint CurrentActiveSceneIndex { get; private set; } = 0;

        /// <summary>
        /// Adds a scene during runtime.
        /// The index is REQUIRED to be unique AND the same across all instances.
        /// </summary>
        /// <param name="sceneName">Scene name.</param>
        /// <param name="index">Index.</param>
        public static void AddRuntimeSceneName(string sceneName, uint index)
        {
            if (!NetworkingManager.Singleton.NetworkConfig.AllowRuntimeSceneChanges)
            {
                throw new NetworkConfigurationException("Cannot change the scene configuration when AllowRuntimeSceneChanges is false");
            }

            registeredSceneNames.Add(sceneName);
            sceneIndexToString.Add(index, sceneName);
            sceneNameToIndex.Add(sceneName, index);
        }

        /// <summary>
        /// Switches to a scene with a given name. Can only be called from Server
        /// </summary>
        /// <param name="sceneName">The name of the scene to switch to</param>
        public static SceneSwitchProgress SwitchScene(string sceneName)
        {
            if (!NetworkingManager.Singleton.IsServer)
            {
                throw new NotServerException("Only server can start a scene switch");
            }
            else if (isSwitching)
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Scene switch already in progress");
                return null;
            }
            else if (!registeredSceneNames.Contains(sceneName))
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("The scene " + sceneName + " is not registered as a switchable scene.");
                return null;
            }

            SpawnManager.ServerDestroySpawnedSceneObjects(); //Destroy current scene objects before switching.
            isSwitching = true;
            lastScene = SceneManager.GetActiveScene();

            SceneSwitchProgress switchSceneProgress = new SceneSwitchProgress();
            sceneSwitchProgresses.Add(switchSceneProgress.guid, switchSceneProgress);
            currentSceneSwitchProgressGuid = switchSceneProgress.guid;

            // Move ALL networked objects to the temp scene
            MoveObjectsToDontDestroyOnLoad();

            isSpawnedObjectsPendingInDontDestroyOnLoad = true;

            // Switch scene
            AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            nextSceneName = sceneName;

            sceneLoad.completed += (AsyncOperation asyncOp2) => { OnSceneLoaded(switchSceneProgress.guid, null); };

            switchSceneProgress.SetSceneLoadOperation(sceneLoad);

            return switchSceneProgress;
        }

        /// <summary>
        /// Switches to a scene using custom scene load and unload handler. Can only be called from Server
        /// </summary>
        /// <param name="sceneName">The name of the scene to switch to</param>
        public static void SwitchSceneCustomHandler()
        {
            if (!NetworkingManager.Singleton.IsServer)
            {
                throw new NotServerException("Only server can start a scene switch");
            }
            else if (isSwitching)
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Scene switch already in progress");
                return;
            }
            else if (!onCustomHandlerIsSceneSwitchAllowed()) // TODO We always return 'true' here for now.
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Scene switch is not allowed.");
                return;
            }

            SpawnManager.ServerDestroySpawnedSceneObjects(); //Destroy current scene objects before switching.
            isSwitching = true;

            SceneSwitchProgress switchSceneProgress = new SceneSwitchProgress();
            sceneSwitchProgresses.Add(switchSceneProgress.guid, switchSceneProgress);
            currentSceneSwitchProgressGuid = switchSceneProgress.guid;

            // TODO I don't think we need this
//            isSpawnedObjectsPendingInDontDestroyOnLoad = true;

            onCustomHandlerLoadScene(() => OnSceneLoaded(switchSceneProgress.guid, null));
        }

        // Called on client
        internal static void OnSceneSwitch(uint sceneIndex, Guid switchSceneGuid, Stream objectStream)
        {
            if (useCustomHandler)
            {
                if (!onCustomHandlerIsSceneSwitchAllowed()) // TODO We always return 'true' here for now.
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Server requested a scene switch to a non registered scene");
                    return;
                }
                if (customSceneIndex > sceneIndex)
                {
                    return; //This scene is already loaded. This usually happends at first load
                }

                // TODO I don't think we need this
//                isSpawnedObjectsPendingInDontDestroyOnLoad = true;

                onCustomHandlerLoadScene(() =>
                {
                    OnSceneLoaded(switchSceneGuid, objectStream);
                });
            }
            else
            {
                if (!sceneIndexToString.ContainsKey(sceneIndex) || !registeredSceneNames.Contains(sceneIndexToString[sceneIndex]))
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Server requested a scene switch to a non registered scene");
                    return;
                }
                else if (SceneManager.GetActiveScene().name == sceneIndexToString[sceneIndex])
                {
                    return; //This scene is already loaded. This usually happends at first load
                }
                lastScene = SceneManager.GetActiveScene();

                // Move ALL networked objects to the temp scene
                MoveObjectsToDontDestroyOnLoad();

                isSpawnedObjectsPendingInDontDestroyOnLoad = true;

                string sceneName = sceneIndexToString[sceneIndex];

                AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                nextSceneName = sceneName;

                sceneLoad.completed += (AsyncOperation asyncOp2) =>
                {
                    OnSceneLoaded(switchSceneGuid, objectStream);
                };
            }
        }

        internal static void OnFirstSceneSwitchSync(uint sceneIndex, Guid switchSceneGuid)
        {
            if (useCustomHandler)
            {
                if (!onCustomHandlerIsSceneSwitchAllowed()) // TODO We always return 'true' here for now.
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Server requested a scene switch to a non registered scene");
                    return;
                }
                if (customSceneIndex > sceneIndex)
                {
                    return; //This scene is already loaded. This usually happends at first load
                }

                customSceneIndex++;

                // TODO I don't think we need this
//                isSpawnedObjectsPendingInDontDestroyOnLoad = true;

                onCustomHandlerLoadScene(() =>
                {
                    using (PooledBitStream stream = PooledBitStream.Get())
                    {
                        using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                        {
                            writer.WriteByteArray(switchSceneGuid.ToByteArray());
                            InternalMessageSender.Send(NetworkingManager.Singleton.ServerClientId, MLAPIConstants.MLAPI_CLIENT_SWITCH_SCENE_COMPLETED, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                        }
                    }

                    isSwitching = false;
                });
            }
            else
            {
                if (!sceneIndexToString.ContainsKey(sceneIndex) || !registeredSceneNames.Contains(sceneIndexToString[sceneIndex]))
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Server requested a scene switch to a non registered scene");
                    return;
                }
                else if (SceneManager.GetActiveScene().name == sceneIndexToString[sceneIndex])
                {
                    return; //This scene is already loaded. This usually happends at first load
                }

                lastScene = SceneManager.GetActiveScene();
                string sceneName = sceneIndexToString[sceneIndex];
                nextSceneName = sceneName;
                CurrentActiveSceneIndex = sceneNameToIndex[sceneName];

                isSpawnedObjectsPendingInDontDestroyOnLoad = true;
                SceneManager.LoadScene(sceneName);

                using (PooledBitStream stream = PooledBitStream.Get())
                {
                    using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                    {
                        writer.WriteByteArray(switchSceneGuid.ToByteArray());
                        InternalMessageSender.Send(NetworkingManager.Singleton.ServerClientId, MLAPIConstants.MLAPI_CLIENT_SWITCH_SCENE_COMPLETED, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                    }
                }

                isSwitching = false;
            }
        }

        private static void OnSceneLoaded(Guid switchSceneGuid, Stream objectStream)
        {
            if (useCustomHandler)
            {
                customSceneIndex++;

                // TODO I don't think we need this
//                isSpawnedObjectsPendingInDontDestroyOnLoad = false;
            }
            else
            {
                CurrentActiveSceneIndex = sceneNameToIndex[nextSceneName];
                Scene nextScene = SceneManager.GetSceneByName(nextSceneName);
                SceneManager.SetActiveScene(nextScene);

                // Move all objects to the new scene
                MoveObjectsToScene(nextScene);

                isSpawnedObjectsPendingInDontDestroyOnLoad = false;

                currentSceneIndex = CurrentActiveSceneIndex;
            }


            if (NetworkingManager.Singleton.IsServer)
            {
                OnSceneUnloadServer(switchSceneGuid);
            }
            else
            {
                OnSceneUnloadClient(switchSceneGuid, objectStream);
            }
        }

        private static void OnSceneUnloadServer(Guid switchSceneGuid)
        {
            // Justification: Rare alloc, could(should?) reuse
            List<NetworkedObject> newSceneObjects = new List<NetworkedObject>();

            {
                NetworkedObject[] networkedObjects = MonoBehaviour.FindObjectsOfType<NetworkedObject>();

                for (int i = 0; i < networkedObjects.Length; i++)
                {
                    if (networkedObjects[i].IsSceneObject == null)
                    {
                        SpawnManager.SpawnNetworkedObjectLocally(networkedObjects[i], SpawnManager.GetNetworkObjectId(), true, false, null, null, false, 0, false, true);

                        newSceneObjects.Add(networkedObjects[i]);
                    }
                }
            }


            for (int j = 0; j < NetworkingManager.Singleton.ConnectedClientsList.Count; j++)
            {
                if (NetworkingManager.Singleton.ConnectedClientsList[j].ClientId != NetworkingManager.Singleton.ServerClientId)
                {
                    using (PooledBitStream stream = PooledBitStream.Get())
                    {
                        using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                        {
                            writer.WriteUInt32Packed(useCustomHandler ? customSceneIndex : CurrentActiveSceneIndex);
                            writer.WriteByteArray(switchSceneGuid.ToByteArray());

                            uint sceneObjectsToSpawn = 0;
                            for (int i = 0; i < newSceneObjects.Count; i++)
                            {
                                if (newSceneObjects[i].observers.Contains(NetworkingManager.Singleton.ConnectedClientsList[j].ClientId))
                                    sceneObjectsToSpawn++;
                            }

                            writer.WriteUInt32Packed(sceneObjectsToSpawn);

                            for (int i = 0; i < newSceneObjects.Count; i++)
                            {
                                if (newSceneObjects[i].observers.Contains(NetworkingManager.Singleton.ConnectedClientsList[j].ClientId))
                                {
                                    writer.WriteBool(newSceneObjects[i].IsPlayerObject);
                                    writer.WriteUInt64Packed(newSceneObjects[i].NetworkId);
                                    writer.WriteUInt64Packed(newSceneObjects[i].OwnerClientId);

                                    NetworkedObject parent = null;

                                    if (!newSceneObjects[i].AlwaysReplicateAsRoot && newSceneObjects[i].transform.parent != null)
                                    {
                                        parent = newSceneObjects[i].transform.parent.GetComponent<NetworkedObject>();
                                    }

                                    if (parent == null)
                                    {
                                        writer.WriteBool(false);
                                    }
                                    else
                                    {
                                        writer.WriteBool(true);
                                        writer.WriteUInt64Packed(parent.NetworkId);
                                    }

                                    if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                                    {
                                        writer.WriteUInt64Packed(newSceneObjects[i].PrefabHash);

                                        writer.WriteSinglePacked(newSceneObjects[i].transform.position.x);
                                        writer.WriteSinglePacked(newSceneObjects[i].transform.position.y);
                                        writer.WriteSinglePacked(newSceneObjects[i].transform.position.z);

                                        writer.WriteSinglePacked(newSceneObjects[i].transform.rotation.eulerAngles.x);
                                        writer.WriteSinglePacked(newSceneObjects[i].transform.rotation.eulerAngles.y);
                                        writer.WriteSinglePacked(newSceneObjects[i].transform.rotation.eulerAngles.z);
                                    }
                                    else
                                    {
                                        writer.WriteUInt64Packed(newSceneObjects[i].NetworkedInstanceId);
                                    }

                                    if (NetworkingManager.Singleton.NetworkConfig.EnableNetworkedVar)
                                    {
                                        newSceneObjects[i].WriteNetworkedVarData(stream, NetworkingManager.Singleton.ConnectedClientsList[j].ClientId);
                                    }
                                }
                            }
                        }

                        InternalMessageSender.Send(NetworkingManager.Singleton.ConnectedClientsList[j].ClientId, MLAPIConstants.MLAPI_SWITCH_SCENE, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                    }
                }
            }

            //Tell server that scene load is completed
            if (NetworkingManager.Singleton.IsHost)
            {
                OnClientSwitchSceneCompleted(NetworkingManager.Singleton.LocalClientId, switchSceneGuid);
            }

            isSwitching = false;

            if (OnSceneSwitched != null)
            {
                OnSceneSwitched();
            }
        }

        private static void OnSceneUnloadClient(Guid switchSceneGuid, Stream objectStream)
        {
            if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
            {
                SpawnManager.DestroySceneObjects();

                using (PooledBitReader reader = PooledBitReader.Get(objectStream))
                {
                    uint newObjectsCount = reader.ReadUInt32Packed();

                    for (int i = 0; i < newObjectsCount; i++)
                    {
                        bool isPlayerObject = reader.ReadBool();
                        ulong networkId = reader.ReadUInt64Packed();
                        ulong owner = reader.ReadUInt64Packed();
                        bool hasParent = reader.ReadBool();
                        ulong? parentNetworkId = null;

                        if (hasParent)
                        {
                            parentNetworkId = reader.ReadUInt64Packed();
                        }

                        ulong prefabHash = reader.ReadUInt64Packed();

                        Vector3 position = new Vector3(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                        Quaternion rotation = Quaternion.Euler(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());

                        NetworkedObject networkedObject = SpawnManager.CreateLocalNetworkedObject(false, 0, prefabHash, parentNetworkId, position, rotation);
                        SpawnManager.SpawnNetworkedObjectLocally(networkedObject, networkId, true, isPlayerObject, owner, objectStream, false, 0, true, false);
                    }
                }
            }
            else
            {
                NetworkedObject[] networkedObjects = MonoBehaviour.FindObjectsOfType<NetworkedObject>();

                SpawnManager.ClientCollectSoftSyncSceneObjectSweep(networkedObjects);

                using (PooledBitReader reader = PooledBitReader.Get(objectStream))
                {
                    uint newObjectsCount = reader.ReadUInt32Packed();

                    for (int i = 0; i < newObjectsCount; i++)
                    {
                        bool isPlayerObject = reader.ReadBool();
                        ulong networkId = reader.ReadUInt64Packed();
                        ulong owner = reader.ReadUInt64Packed();
                        bool hasParent = reader.ReadBool();
                        ulong? parentNetworkId = null;

                        if (hasParent)
                        {
                            parentNetworkId = reader.ReadUInt64Packed();
                        }

                        ulong instanceId = reader.ReadUInt64Packed();

                        NetworkedObject networkedObject = SpawnManager.CreateLocalNetworkedObject(true, instanceId, 0, parentNetworkId, null, null);
                        SpawnManager.SpawnNetworkedObjectLocally(networkedObject, networkId, true, isPlayerObject, owner, objectStream, false, 0, true, false);
                    }
                }
            }

            using (PooledBitStream stream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                {
                    writer.WriteByteArray(switchSceneGuid.ToByteArray());
                    NetworkedObject networkedObject = null;
                    InternalMessageSender.Send(NetworkingManager.Singleton.ServerClientId, MLAPIConstants.MLAPI_CLIENT_SWITCH_SCENE_COMPLETED, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, networkedObject);
                }
            }

            isSwitching = false;

            if (OnSceneSwitched != null)
            {
                OnSceneSwitched();
            }
        }

        internal static bool HasSceneMismatch(uint sceneIndex)
        {
            if (useCustomHandler)
            {
                // TODO Pretend it's all fine for now. I think this check can and should be used to let
                // TODO the user decide how to determine scene mismatch because he/she is now in charge of
                // TODO loading and unloading scenes (additively or not doesn't matter).
                return onCustomHandlerHasSceneMismatch(); // TODO We always return 'false' here for now.
            }
            else
            {
                return SceneManager.GetActiveScene().name != sceneIndexToString[sceneIndex];
            }
        }

        // Called on server
        internal static void OnClientSwitchSceneCompleted(ulong clientId, Guid switchSceneGuid)
        {
            if (switchSceneGuid == Guid.Empty)
            {
                //If Guid is empty it means the client has loaded the start scene of the server and the server would never have a switchSceneProgresses created for the start scene.
                return;
            }
            if (!sceneSwitchProgresses.ContainsKey(switchSceneGuid))
            {
                return;
            }

            sceneSwitchProgresses[switchSceneGuid].AddClientAsDone(clientId);
        }


        internal static void RemoveClientFromSceneSwitchProgresses(ulong clientId)
        {
            foreach (SceneSwitchProgress switchSceneProgress in sceneSwitchProgresses.Values)
            {
                switchSceneProgress.RemoveClientAsDone(clientId);
            }
        }

        private static void MoveObjectsToDontDestroyOnLoad()
        {
            // Move ALL networked objects to the temp scene
            List<NetworkedObject> objectsToKeep = SpawnManager.SpawnedObjectsList;

            for (int i = 0; i < objectsToKeep.Count; i++)
            {
                //In case an object has been set as a child of another object it has to be unchilded in order to be moved from one scene to another.
                if (objectsToKeep[i].gameObject.transform.parent != null)
                {
                    objectsToKeep[i].gameObject.transform.parent = null;
                }

                MonoBehaviour.DontDestroyOnLoad(objectsToKeep[i].gameObject);
            }
        }

        private static void MoveObjectsToScene(Scene scene)
        {
            // Move ALL networked objects to the temp scene
            List<NetworkedObject> objectsToKeep = SpawnManager.SpawnedObjectsList;

            for (int i = 0; i < objectsToKeep.Count; i++)
            {
                //In case an object has been set as a child of another object it has to be unchilded in order to be moved from one scene to another.
                if (objectsToKeep[i].gameObject.transform.parent != null)
                {
                    objectsToKeep[i].gameObject.transform.parent = null;
                }

                SceneManager.MoveGameObjectToScene(objectsToKeep[i].gameObject, scene);
            }
        }
    }
}
