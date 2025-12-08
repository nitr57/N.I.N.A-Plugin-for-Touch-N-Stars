using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NINA.Core.Utility;

namespace TouchNStars.Server.Services
{
    /// <summary>
    /// Service for accessing PHD2 camera list and selected camera via POSIX shared memory
    /// Provides interface to shm_camera.h functionality
    /// </summary>
    public class PHD2SHMService : IDisposable
    {
        // Constants from shm_camera.h
        private const string PHD2_CAMERA_SHM_NAME = "/phd2_cameras";
        private const uint INVALID_CAMERA_INDEX = 0xFFFFFFFF;
        private const int MAX_CAMERAS_SHM = 64;
        private const int MAX_CAMERA_NAME_LEN = 256;
        private const int MAX_CAMERA_INSTANCES = 64;

        // P/Invoke declarations for shm_camera functions from libshm_guider
        [DllImport("libshm_guider.so", SetLastError = true)]
        private static extern IntPtr shm_open(string name, int oflag, uint mode);

        [DllImport("libshm_guider.so", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libshm_guider.so", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, nint offset);

        [DllImport("libshm_guider.so", SetLastError = true)]
        private static extern int munmap(IntPtr addr, UIntPtr length);

        [DllImport("libshm_guider.so", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr shm_mount_init(int create_if_missing);

        [DllImport("libshm_guider.so", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr shm_camera_config_get_readonly();

        [DllImport("libshm_guider.so", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern void shm_camera_config_release_readonly(IntPtr shm);

        [DllImport("libshm_guider.so", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int shm_camera_config_get_option(string option_name, out int value);

        [DllImport("libshm_guider.so", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int shm_camera_config_set_option(IntPtr shm, string option_name, int value);

        // CameraInstance structure matching shm_camera.h
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CameraInstance
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_CAMERA_NAME_LEN)]
            public string id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_CAMERA_NAME_LEN)]
            public string display_name;
        }

        // CameraEntry structure matching shm_camera.h
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CameraEntry
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_CAMERA_NAME_LEN)]
            public string name;
        }

        // EquipmentEntry structure for mounts (matching shm_mount.h)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct EquipmentEntry
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_CAMERA_NAME_LEN)]
            public string name;
        }

        // CameraConfigOption structure matching shm_camera_config.h
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct CameraConfigOption
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string name;
            public int value;
            public int min_value;
            public int max_value;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] reserved;
        }

        // CameraConfigSHM structure matching shm_camera_config.h
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct CameraConfigSHM
        {
            public uint magic;
            public uint version;
            public uint num_options;
            public uint update_counter;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
            public byte[] reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public CameraConfigOption[] options;
        }

        // CameraListSHM structure matching shm_camera.h
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct CameraListSHM
        {
            public uint version;
            public uint num_cameras;
            public uint selected_camera_index;
            public uint timestamp;
            public uint list_update_counter;
            public uint selected_change_counter;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_CAMERA_NAME_LEN)]
            public string selected_camera_id;
            public uint num_instances;
            public uint can_select_camera;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CAMERA_INSTANCES)]
            public CameraInstance[] instances;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
            public byte[] reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_CAMERAS_SHM)]
            public CameraEntry[] cameras;
        }

        private IntPtr shmAddr = IntPtr.Zero;
        private int shmFd = -1;
        private UIntPtr shmSize;
        private bool disposed = false;

        public PHD2SHMService()
        {
            InitializeSharedMemory();
        }

        private void InitializeSharedMemory()
        {
            try
            {
                // Open the shared memory segment (read-only for client)
                const int O_RDWR = 2;
                IntPtr result = shm_open(PHD2_CAMERA_SHM_NAME, O_RDWR, 0);
                shmFd = (int)(long)result;

                if (shmFd < 0)
                {
                    Logger.Debug($"Could not open shared memory {PHD2_CAMERA_SHM_NAME}");
                    return;
                }

                // Map shared memory
                uint structSize = (uint)Marshal.SizeOf(typeof(CameraListSHM));
                shmSize = new UIntPtr(structSize);
                const int PROT_READ = 1;
                const int PROT_WRITE = 2;
                const int MAP_SHARED = 1;

                shmAddr = mmap(IntPtr.Zero, shmSize, PROT_READ | PROT_WRITE, MAP_SHARED, shmFd, 0);

                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    Logger.Debug("Failed to map shared memory");
                    close(shmFd);
                    shmFd = -1;
                    return;
                }

                Logger.Debug("Successfully initialized PHD2 shared memory");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing PHD2 SHM: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the list of available cameras from shared memory
        /// </summary>
        public List<string> GetCameraList()
        {
            var cameras = new List<string>();

            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    Logger.Debug("Shared memory not initialized");
                    return cameras;
                }

                // Marshal the shared memory to our structure
                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);

                if (shmData.cameras != null && shmData.num_cameras > 0)
                {
                    for (int i = 0; i < shmData.num_cameras && i < shmData.cameras.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(shmData.cameras[i].name))
                        {
                            cameras.Add(shmData.cameras[i].name);
                        }
                    }
                }

                Logger.Debug($"Retrieved {cameras.Count} cameras from SHM");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting camera list from SHM: {ex.Message}");
            }

            return cameras;
        }

        /// <summary>
        /// Get the index of the currently selected camera
        /// </summary>
        public uint? GetSelectedCameraIndex()
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    return null;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);

                if (shmData.selected_camera_index == INVALID_CAMERA_INDEX)
                {
                    return null;
                }

                return shmData.selected_camera_index;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting selected camera index from SHM: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get the ID of the currently selected camera
        /// </summary>
        public string GetSelectedCameraId()
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    return null;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);
                return string.IsNullOrEmpty(shmData.selected_camera_id) ? null : shmData.selected_camera_id;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting selected camera ID from SHM: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set the selected camera by index
        /// </summary>
        public bool SetSelectedCameraIndex(uint index)
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    Logger.Debug("Shared memory not initialized");
                    return false;
                }

                // Read current structure
                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);

                // Validate index
                if (index >= shmData.num_cameras)
                {
                    Logger.Error($"Camera index {index} out of range (max: {shmData.num_cameras})");
                    return false;
                }

                // Update selected index
                shmData.selected_camera_index = index;
                shmData.selected_change_counter++;
                shmData.timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                // Write back to shared memory
                Marshal.StructureToPtr(shmData, shmAddr, false);

                Logger.Debug($"Set selected camera index to {index}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting selected camera index in SHM: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get available instances for the selected camera
        /// </summary>
        public List<string> GetCameraInstances()
        {
            var instances = new List<string>();

            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    return instances;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);

                if (shmData.instances != null && shmData.num_instances > 0)
                {
                    for (int i = 0; i < shmData.num_instances && i < shmData.instances.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(shmData.instances[i].display_name))
                        {
                            instances.Add(shmData.instances[i].display_name);
                        }
                    }
                }

                Logger.Debug($"Retrieved {instances.Count} instances from SHM");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting camera instances from SHM: {ex.Message}");
            }

            return instances;
        }

        /// <summary>
        /// Check if instance selection is available for the selected camera
        /// </summary>
        public bool IsInstanceSelectionAvailable()
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    return false;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);
                return shmData.can_select_camera != 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error checking instance selection availability: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;

            try
            {
                if (shmAddr != IntPtr.Zero && shmAddr != new IntPtr(-1))
                {
                    uint structSize = (uint)Marshal.SizeOf(typeof(CameraListSHM));
                    UIntPtr sizeToUnmap = new UIntPtr(structSize);
                    munmap(shmAddr, sizeToUnmap);
                    shmAddr = IntPtr.Zero;
                }

                if (shmFd >= 0)
                {
                    close(shmFd);
                    shmFd = -1;
                }

                disposed = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disposing PHD2SHMService: {ex.Message}");
            }
        }

        ~PHD2SHMService()
        {
            Dispose();
        }

        // Additional methods for mount and camera options support
        public List<string> GetMountList()
        {
            var mounts = new List<string>();

            try
            {
                IntPtr mountShmAddr = shm_mount_init(0); // 0 = don't create if missing
                if (mountShmAddr == IntPtr.Zero)
                {
                    Logger.Debug("Mount shared memory not initialized");
                    return mounts;
                }

                // Marshal the mount list structure
                uint structSize = (uint)Marshal.SizeOf(typeof(CameraListSHM)); // Reuse same structure
                var mountData = Marshal.PtrToStructure<CameraListSHM>(mountShmAddr);

                if (mountData.cameras != null && mountData.num_cameras > 0)
                {
                    for (int i = 0; i < mountData.num_cameras && i < mountData.cameras.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(mountData.cameras[i].name))
                        {
                            mounts.Add(mountData.cameras[i].name);
                        }
                    }
                }

                Logger.Debug($"Retrieved {mounts.Count} mounts from SHM");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting mount list from SHM: {ex.Message}");
            }

            return mounts;
        }

        public uint? GetSelectedMountIndex()
        {
            try
            {
                IntPtr mountShmAddr = shm_mount_init(0); // 0 = don't create if missing
                if (mountShmAddr == IntPtr.Zero)
                {
                    return null;
                }

                var mountData = Marshal.PtrToStructure<CameraListSHM>(mountShmAddr);

                if (mountData.selected_camera_index == INVALID_CAMERA_INDEX)
                {
                    return null;
                }

                return mountData.selected_camera_index;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting selected mount index from SHM: {ex.Message}");
                return null;
            }
        }

        public bool SetSelectedMountIndex(uint index)
        {
            try
            {
                IntPtr mountShmAddr = shm_mount_init(0); // 0 = don't create if missing
                if (mountShmAddr == IntPtr.Zero)
                {
                    Logger.Debug("Mount shared memory not initialized");
                    return false;
                }

                var mountData = Marshal.PtrToStructure<CameraListSHM>(mountShmAddr);

                if (index >= mountData.num_cameras)
                {
                    Logger.Error($"Mount index {index} out of range (max: {mountData.num_cameras})");
                    return false;
                }

                mountData.selected_camera_index = index;
                mountData.selected_change_counter++;
                mountData.timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                Marshal.StructureToPtr(mountData, mountShmAddr, false);

                Logger.Debug($"Set selected mount index to {index}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting selected mount index in SHM: {ex.Message}");
                return false;
            }
        }

        public List<string> GetCameraOptions()
        {
            var options = new List<string>();

            try
            {
                IntPtr configShmAddr = shm_camera_config_get_readonly();
                if (configShmAddr == IntPtr.Zero)
                {
                    Logger.Debug("Camera config shared memory not available");
                    return options;
                }

                try
                {
                    // Marshal the entire structure
                    var configData = Marshal.PtrToStructure<CameraConfigSHM>(configShmAddr);

                    if (configData.options != null && configData.num_options > 0)
                    {
                        for (int i = 0; i < configData.num_options && i < configData.options.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(configData.options[i].name))
                            {
                                options.Add(configData.options[i].name.TrimEnd('\0').Trim());
                            }
                        }
                    }

                    Logger.Debug($"Retrieved {options.Count} camera options from SHM");
                }
                finally
                {
                    shm_camera_config_release_readonly(configShmAddr);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting camera options from SHM: {ex.Message}");
            }

            return options;
        }

        public string GetCameraOptionValue(string optionName)
        {
            try
            {
                if (shm_camera_config_get_option(optionName, out int value) == 0)
                {
                    return value.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting camera option '{optionName}' from SHM: {ex.Message}");
            }

            return null;
        }

        public bool SetCameraOptionValue(string optionName, string value)
        {
            try
            {
                if (!int.TryParse(value, out int intValue))
                {
                    Logger.Error($"Invalid option value for '{optionName}': {value}");
                    return false;
                }

                IntPtr configShmAddr = shm_camera_config_get_readonly();
                if (configShmAddr == IntPtr.Zero)
                {
                    Logger.Debug("Camera config shared memory not available");
                    return false;
                }

                try
                {
                    // For setting, we need write access. Since we only have read-only, 
                    // we'll try to set it directly through the function
                    int result = shm_camera_config_set_option(configShmAddr, optionName, intValue);
                    return result == 0;
                }
                finally
                {
                    shm_camera_config_release_readonly(configShmAddr);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting camera option '{optionName}' in SHM: {ex.Message}");
                return false;
            }
        }

        public string GetSelectedCameraInstance()
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    return null;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);
                return string.IsNullOrEmpty(shmData.selected_camera_id) ? null : shmData.selected_camera_id;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting selected camera instance from SHM: {ex.Message}");
                return null;
            }
        }

        public bool SetSelectedCameraInstanceId(string instanceId)
        {
            try
            {
                if (shmAddr == IntPtr.Zero || shmAddr == new IntPtr(-1))
                {
                    Logger.Debug("Shared memory not initialized");
                    return false;
                }

                var shmData = Marshal.PtrToStructure<CameraListSHM>(shmAddr);
                shmData.selected_camera_id = instanceId ?? "";
                shmData.selected_change_counter++;
                shmData.timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                Marshal.StructureToPtr(shmData, shmAddr, false);

                Logger.Debug($"Set selected camera instance ID to {instanceId}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting selected camera instance ID in SHM: {ex.Message}");
                return false;
            }
        }
    }
}
