using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SMBLibrary;
using SMBLibrary.Client;

namespace SmbDiag
{
    public class SmbScannerTest
    {
        public void Run(ScanSource source)
        {
            Console.WriteLine($"\n--- Testing SMB Source: {source.Name} ---");
            Console.WriteLine($"Configuration Path: {source.Path}");

            var client = new SMB2Client();
            try
            {
                string host = "";
                string share = "";
                string baseDir = "";

                // Parsing Logic
                if (source.StorageCredential != null && !string.IsNullOrEmpty(source.StorageCredential.Host))
                {
                    host = source.StorageCredential.Host;
                    string rawPath = source.Path;
                    
                    if (rawPath.StartsWith("smb://"))
                    {
                         var uri = new Uri(rawPath);
                         if (uri.Segments.Length > 1) {
                             share = uri.Segments[1].Trim('/');
                             if (uri.Segments.Length > 2) {
                                baseDir = string.Join("", uri.Segments.Skip(2)).Trim('/');
                                baseDir = baseDir.Replace('/', '\\');
                             }
                         }
                    }
                    else
                    {
                        // Match Program.cs logic exactly (Step 342)
                        // Heuristic: If share contains slash, split it.
                        if (rawPath.Contains('/') || rawPath.Contains('\\'))
                        {
                            var parts = rawPath.Split(new[] { '/', '\\' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0) 
                            {
                                share = parts[0];
                                if (parts.Length > 1) baseDir = parts[1];
                            }
                        }
                        else
                        {
                            share = rawPath;
                        }
                    }
                }
                else
                {
                     // Legacy
                     var uri = new Uri(source.Path); 
                     host = uri.Host;
                     share = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : string.Empty;
                     if (uri.Segments.Length > 2)
                     {
                         baseDir = string.Join("", uri.Segments.Skip(2)).Trim('/');
                         baseDir = baseDir.Replace('/', '\\');
                     }
                }

                Console.WriteLine($"Resolved -> Host: {host}, Share: {share}, BaseDir: {baseDir}");

                if (!client.Connect(host, SMBTransportType.DirectTCPTransport))
                {
                    Console.WriteLine("[ERROR] Failed to connect to host (TCP).");
                    return;
                }

                string u = "", p = "";
                if (source.StorageCredential != null)
                {
                    try {
                        var doc = JsonDocument.Parse(source.StorageCredential.AuthData);
                        if(doc.RootElement.TryGetProperty("username", out var je)) u = je.GetString();
                        if(doc.RootElement.TryGetProperty("password", out var je2)) p = je2.GetString();
                    } catch {}
                }

                var status = client.Login(string.Empty, u, p);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    Console.WriteLine($"[ERROR] Login Failed: {status}");
                    return;
                }

                ISMBFileStore fileStore = client.TreeConnect(share, out status);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    Console.WriteLine($"[ERROR] TreeConnect Failed: {status}");
                    return;
                }
                
                Console.WriteLine($"[OK] Connected. Scanning starting at: {baseDir}");
                
                var stats = new ScanStats();
                ListFilesRecursive((SMB2FileStore)fileStore, baseDir, 0, stats);
                
                Console.WriteLine("\n=== 扫描结果 Scan Result ===");
                Console.WriteLine($"扫描路径: {baseDir}");
                Console.WriteLine($"文件总数: {stats.FileCount}");
                Console.WriteLine($"目录总数: {stats.DirCount}");
                Console.WriteLine($"最大递归深度: {stats.MaxDepth}");
                Console.WriteLine("===================");

                client.Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXCEPTION] {ex.Message}");
            }
        }

        private void ListFilesRecursive(SMB2FileStore fileStore, string path, int depth, ScanStats stats)
        {
             if (depth > stats.MaxDepth) stats.MaxDepth = depth;

             NTStatus status;
             object handle;
             
             status = fileStore.CreateFile(out handle, out _, path, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
             
             if (status != NTStatus.STATUS_SUCCESS)
             {
                 // Console.WriteLine($"[ERROR] OpenDir '{path}': {status}");
                 return;
             }

             List<QueryDirectoryFileInformation> fileList;
             status = fileStore.QueryDirectory(out fileList, handle, "*", FileInformationClass.FileDirectoryInformation);
             fileStore.CloseFile(handle);

             if (fileList != null)
             {
                 stats.DirCount++; 
                 // Console.WriteLine($"[DEBUG] Scanning {path}: Found {fileList.Count} items.");
                 
                 foreach (var item in fileList)
                 {
                     if (item is FileDirectoryInformation fileInfo)
                     {
                         if (fileInfo.FileName == "." || fileInfo.FileName == "..") continue;

                         string fullPath = string.IsNullOrEmpty(path) ? fileInfo.FileName : path + "\\" + fileInfo.FileName;

                         if ((fileInfo.FileAttributes & SMBLibrary.FileAttributes.Directory) == SMBLibrary.FileAttributes.Directory)
                         {
                             // Console.WriteLine($"[DIR] {fullPath}");
                             ListFilesRecursive(fileStore, fullPath, depth + 1, stats);
                         }
                         else
                         {
                             stats.FileCount++;
                             // Console.WriteLine($"[FILE] {fullPath}");
                         }
                     }
                 }
             }
        }
    }

    public class ScanStats
    {
        public int FileCount { get; set; }
        public int DirCount { get; set; }
        public int MaxDepth { get; set; }
    }
}
