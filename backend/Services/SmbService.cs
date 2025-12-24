using SMBLibrary;
using SMBLibrary.Client;
using System.Net;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public interface ISmbService
{
    bool Connect(ScanSource source, out ISMBClient client, out SMB2FileStore fileStore);
    void Disconnect(ISMBClient client);
    IEnumerable<string> ListFiles(ScanSource source, string relativePath);
    List<BrowsableItem> ListContents(StorageCredential credential, string path);
    Stream? OpenFile(ScanSource source, string filePath);
    Stream? OpenWriteFile(ScanSource source, string filePath);
    bool CreateDirectory(ScanSource source, string dirPath);
    bool Delete(ScanSource source, string path, bool isDirectory);
    bool TestCredentials(StorageCredential credential);
}

public class SmbService : ISmbService
{
    private readonly ILogger<SmbService> _logger;

    public SmbService(ILogger<SmbService> logger)
    {
        _logger = logger;
    }

    public bool TestCredentials(StorageCredential credential)
    {
        var client = new SMB2Client();
        try
        {
            if (string.IsNullOrEmpty(credential.Host)) return false;
            
            // Handle smb:// prefix if present
            string host = credential.Host;
            if (host.StartsWith("smb://"))
            {
                try { host = new Uri(host).Host; }
                catch { host = host.Replace("smb://", "").Trim('/'); }
            }

            bool isConnected = client.Connect(host, SMBTransportType.DirectTCPTransport);
            if (!isConnected) 
            {
                _logger.LogError($"TestCredentials: Connect failed to host '{host}'");
                return false;
            }

            string username = "";
            string password = "";
            try 
            {
                using var doc = System.Text.Json.JsonDocument.Parse(credential.AuthData);
                if (doc.RootElement.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogError($"TestCredentials: JSON Parse Failed: {ex.Message}");
            }

            var status = client.Login(string.Empty, username, password);
            client.Disconnect();
            
            if (status != NTStatus.STATUS_SUCCESS)
            {
                _logger.LogError($"TestCredentials: Login Failed. Status: {status}, User: {username}");
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"TestCredentials: Exception: {ex.Message}");
            return false;
        }
    }

    public bool Connect(ScanSource source, out ISMBClient client, out SMB2FileStore fileStore)
    {
        string share = "";
        ParseSourcePath(source, out _, out share, out _);
        return Connect(source.StorageCredential, share, out client, out fileStore);
    }

    public bool Connect(StorageCredential? credential, string shareName, out ISMBClient client, out SMB2FileStore fileStore)
    {
        client = new SMB2Client();
        fileStore = null!;

        try
        {
            if (credential == null || string.IsNullOrEmpty(credential.Host)) 
            {
                _logger.LogError("Connect: Missing credential or host");
                return false;
            }

            string host = credential.Host;
            // Handle smb:// prefix
            if (host.StartsWith("smb://"))
            {
                try { host = new Uri(host).Host; }
                catch { host = host.Replace("smb://", "").Trim('/'); }
            }

            bool isConnected = client.Connect(host, SMBTransportType.DirectTCPTransport);
            if (!isConnected) 
            {
                _logger.LogError($"Failed to connect to SMB server {host}");
                return false;
            }

            string username = "";
            string password = "";
            try 
            {
                using var doc = System.Text.Json.JsonDocument.Parse(credential.AuthData);
                if (doc.RootElement.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to parse credentials: {ex.Message}");
            }

            NTStatus status = client.Login(string.Empty, username, password);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                _logger.LogError($"SMB Login failed: {status}");
                return false;
            }

            if (string.IsNullOrEmpty(shareName))
            {
                // If share empty, we can't TreeConnect. Just return client connected.
                // But this method signature expects fileStore.
                // So return false if share is missing.
                 _logger.LogError($"SMB Connect: Share name required for TreeConnect");
                 return false;
            }

            ISMBFileStore store = client.TreeConnect(shareName, out status);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                _logger.LogError($"SMB TreeConnect failed to share '{shareName}': {status}");
                return false;
            }
            
            fileStore = (SMB2FileStore)store;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"SMB Connect Exception: {ex.Message}");
            return false;
        }
    }

    public List<string> ListShares(StorageCredential credential)
    {
        var shares = new List<string>();
        var client = new SMB2Client();
        try 
        {
            string host = credential.Host;
            if (host.StartsWith("smb://"))
            {
                try { host = new Uri(host).Host; }
                catch { host = host.Replace("smb://", "").Trim('/'); }
            }

            if (!client.Connect(host, SMBTransportType.DirectTCPTransport)) return shares;
            
            string username = "";
            string password = "";
            try 
            {
                using var doc = System.Text.Json.JsonDocument.Parse(credential.AuthData);
                if (doc.RootElement.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
            }
            catch {}

            if (client.Login(string.Empty, username, password) != NTStatus.STATUS_SUCCESS) return shares;

            // ISMBClient.ListShares returns List<string> and outputs status
            var shareList = client.ListShares(out NTStatus status);
            if (status == NTStatus.STATUS_SUCCESS && shareList != null)
            {
                shares.AddRange(shareList);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"ListShares Exception: {ex.Message}");
        }
        finally
        {
            client.Disconnect();
        }
        return shares;
    }

    public List<BrowsableItem> ListContents(StorageCredential credential, string path)
    {
        var results = new List<BrowsableItem>();

        // Path format: ShareName/Folder/SubFolder
        // Or if empty -> List Shares
        
        // Normalize path
        path = path.Replace('\\', '/').Trim('/');

        if (string.IsNullOrEmpty(path))
        {
            // List Shares
            var shares = ListShares(credential);
            return shares.Select(s => new BrowsableItem { Name = s, Type = "Share", Path = s }).ToList();
        }

        // Split Share and RelPath
        var parts = path.Split('/', 2);
        string share = parts[0];
        string relPath = parts.Length > 1 ? parts[1] : "";
        relPath = relPath.Replace('/', '\\');

        if (Connect(credential, share, out var client, out var fileStore))
        {
            try
            {
                object handle;
                // Use "*" pattern for directory listing
                string searchPath = string.IsNullOrEmpty(relPath) ? "" : relPath;
                
                NTStatus status = fileStore.CreateFile(out handle, out _, searchPath, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
                
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    status = fileStore.QueryDirectory(out var fileList, handle, "*", FileInformationClass.FileDirectoryInformation);
                    fileStore.CloseFile(handle);

                    if (fileList != null)
                    {
                        foreach (var item in fileList)
                        {
                            if (item is FileDirectoryInformation fileInfo)
                            {
                                if (fileInfo.FileName == "." || fileInfo.FileName == "..") continue;
                                
                                bool isDir = (fileInfo.FileAttributes & SMBLibrary.FileAttributes.Directory) == SMBLibrary.FileAttributes.Directory;
                                results.Add(new BrowsableItem
                                {
                                    Name = fileInfo.FileName,
                                    Type = isDir ? "Directory" : "File",
                                    Path = path + "/" + fileInfo.FileName
                                });
                            }
                        }
                    }
                }
            }
            finally
            {
                client.Disconnect();
            }
        }
        
        return results.OrderByDescending(x => x.Type == "Directory").ThenBy(x => x.Name).ToList();
    }
    
    // Existing Methods...
    public void Disconnect(ISMBClient client)
    {
        client.Disconnect();
    }

    private void ListFilesRecursive(SMB2FileStore fileStore, string path, List<string> results)
    {
        object handle;
        // Ensure SMB uses backslashes for querying
        string queryPath = path.Replace('/', '\\');
        NTStatus status = fileStore.CreateFile(out handle, out _, queryPath, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
        
        if (status == NTStatus.STATUS_SUCCESS)
        {
            List<QueryDirectoryFileInformation> fileList;
            status = fileStore.QueryDirectory(out fileList, handle, "*", FileInformationClass.FileDirectoryInformation);
            fileStore.CloseFile(handle);

            if (fileList != null)
            {
                foreach (var item in fileList)
                {
                    if (item is FileDirectoryInformation fileInfo)
                    {
                        if (fileInfo.FileName == "." || fileInfo.FileName == "..") continue;

                        // Use forward slash for internal representation (works on Mac/Linux/Windows .NET)
                        string fullPath = string.IsNullOrEmpty(path) ? fileInfo.FileName : path + "/" + fileInfo.FileName;

                        if ((fileInfo.FileAttributes & SMBLibrary.FileAttributes.Directory) == SMBLibrary.FileAttributes.Directory)
                        {
                            ListFilesRecursive(fileStore, fullPath, results);
                        }
                        else
                        {
                            if (GenericMediaFilter(fileInfo.FileName))
                            {
                                results.Add(fullPath);
                            }
                        }
                    }
                }
            }
        }
    }

    private bool GenericMediaFilter(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".wav" || ext == ".ogg" || ext == ".opus";
    }

    private void ParseSourcePath(ScanSource source, out string server, out string share, out string baseDir)
    {
        server = "";
        share = "";
        baseDir = "";

        if (source.StorageCredential != null && !string.IsNullOrEmpty(source.StorageCredential.Host))
        {
            server = source.StorageCredential.Host;
            string rawPath = source.Path;
            
            if (rawPath.StartsWith("smb://"))
            {
                 var uri = new Uri(rawPath);
                 if (uri.Segments.Length > 1) {
                     share = uri.Segments[1].Trim('/');
                     if (uri.Segments.Length > 2) {
                        baseDir = string.Join("", uri.Segments.Skip(2)).Trim('/');
                        // Normalize baseDir to forward slash for consistency?
                        // Or keep as is and let ListFiles logic handle it.
                        // Let's normalize here too.
                        baseDir = baseDir.Replace('\\', '/');
                     }
                 }
            }
            else
            {
                // Match Test Tool Logic
                if (rawPath.Contains('/') || rawPath.Contains('\\'))
                {
                    var parts = rawPath.Split(new[] { '/', '\\' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) 
                    {
                        share = parts[0];
                        if (parts.Length > 1) baseDir = parts[1].Replace('\\', '/');
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
             // Legacy Logic
             try {
                 var uri = new Uri(source.Path); 
                 server = uri.Host;
                 share = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : string.Empty;
                 if (uri.Segments.Length > 2)
                 {
                     baseDir = string.Join("", uri.Segments.Skip(2)).Trim('/');
                     baseDir = baseDir.Replace('\\', '/');
                 }
             } catch {
                 // Fallback if not URI
                 server = ""; share = source.Path; 
             }
        }
    }

    public IEnumerable<string> ListFiles(ScanSource source, string relativePath)
    {
        var files = new List<string>();
        if (Connect(source, out var client, out var fileStore))
        {
            try 
            {
                ParseSourcePath(source, out _, out _, out string baseDir);
                
                // Combine config baseDir + requested relativePath
                string startPath = baseDir;
                if (!string.IsNullOrEmpty(relativePath))
                {
                    if (string.IsNullOrEmpty(startPath)) startPath = relativePath.Replace('\\', '/');
                    else startPath = startPath + "/" + relativePath.Replace('\\', '/');
                }

                ListFilesRecursive(fileStore, startPath, files);
            }
            finally
            {
                Disconnect(client);
            }
        }
        return files;
    }

    public Stream? OpenFile(ScanSource source, string filePath)
    {
        if (Connect(source, out var client, out var fileStore))
        {
             try 
             {
                 // Convert to backslash for SMB
                 return new SmbFileStream(client, fileStore, filePath.Replace('/', '\\'));
             }
             catch (Exception ex)
             {
                 _logger.LogError($"OpenFile Exception: {ex.Message}");
                 client.Disconnect();
                 return null;
             }
        }
        return null;
    }

    public Stream? OpenWriteFile(ScanSource source, string filePath)
    {
        if (Connect(source, out var client, out var fileStore))
        {
             try 
             {
                 return new SmbFileStream(client, fileStore, filePath.Replace('/', '\\'), FileAccess.Write);
             }
             catch (Exception ex)
             {
                 _logger.LogError($"OpenWriteFile Exception: {ex.Message}");
                 client.Disconnect();
                 return null;
             }
        }
        return null;
    }

    public bool Delete(ScanSource source, string path, bool isDirectory)
    {
        if (Connect(source, out var client, out var fileStore))
        {
            try
            {
                // Do not prepend baseDir. MediaFile.FilePath is already relative to Share.
                string targetPath = path.Replace('/', '\\');
                
                object handle;
                // OPEN with DELETE access (included in GENERIC_ALL)
                // Note: To delete a directory, it must be empty usually? SMB pending delete usually handles it if empty.
                // If not empty, it returns DirectoryNotEmpty.
                var access = AccessMask.GENERIC_ALL; 
                var attrs = isDirectory ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal;
                var createOpt = isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE;
                
                // Try Open
                var status = fileStore.CreateFile(out handle, out _, targetPath, access, attrs, ShareAccess.None, CreateDisposition.FILE_OPEN, createOpt, null);
                
                if (status != NTStatus.STATUS_SUCCESS)
                {
                     _logger.LogError($"Delete Open Failed: {targetPath} - {status}");
                     return false;
                }
                
                // Set Delete Disposition
                var disposition = new FileDispositionInformation { DeletePending = true };
                status = fileStore.SetFileInformation(handle, disposition);
                
                fileStore.CloseFile(handle); // Delete happens on close
                
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    _logger.LogError($"Delete SetDisposition Failed: {targetPath} - {status}");
                    return false;
                }
                
                _logger.LogInformation($"Deleted: {targetPath}");
                return true;
            }
            finally
            {
                client.Disconnect();
            }
        }
        return false;
    }

    public bool CreateDirectory(ScanSource source, string dirPath)
    {
        if (Connect(source, out var client, out var fileStore))
        {
            try
            {

                string path = dirPath.Replace('/', '\\');
                ParseSourcePath(source, out string server, out string share, out _);
                string basePath = $"smb://{server}/{share}/";

                var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string currentPath = "";

                foreach (var part in parts)
                {
                    currentPath = string.IsNullOrEmpty(currentPath) ? part : currentPath + "\\" + part;
                    
                    object handle;
                    // First try to OPEN (to check existence). If we use FILE_OPEN_IF, we don't know if we created it.
                    // But we want to ensure it works. 
                    // Let's use GENERIC_ALL to ensure we have rights.
                    
                    // Try Open First
                    var status = fileStore.CreateFile(out handle, out _, currentPath, AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
                    
                    if (status == NTStatus.STATUS_SUCCESS)
                    {
                        // Exists
                        _logger.LogInformation($"Directory Exists: {basePath}{currentPath}");
                        fileStore.CloseFile(handle);
                        continue;
                    }
                    
                    // Not exists (or other error). Try Create.
                    status = fileStore.CreateFile(out handle, out _, currentPath, AccessMask.GENERIC_ALL, SMBLibrary.FileAttributes.Directory, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null);
                    
                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                        {
                             _logger.LogInformation($"Directory Exists (Collision): {basePath}{currentPath}");
                             continue;
                        }
                         _logger.LogError($"CreateDirectory Recursive Failed at: {basePath}{currentPath} - Status: {status}");
                         return false;
                    }
                    else
                    {
                         _logger.LogInformation($"Directory Created: {basePath}{currentPath}");
                         fileStore.CloseFile(handle);
                    }
                }
                return true;
            }
            finally
            {
                client.Disconnect();
            }
        }
        return false;
    }
}

public class BrowsableItem
{
    public required string Name { get; set; }
    public required string Type { get; set; } // "Share", "Directory", "File"
    public required string Path { get; set; }
}


public class SmbFileStream : Stream
{
    private readonly ISMBClient _client;
    private readonly SMB2FileStore _store;
    private object? _handle;
    private long _position;
    private long _length;

    private readonly FileAccess _access;

    public SmbFileStream(ISMBClient client, SMB2FileStore store, string path, FileAccess access = FileAccess.Read)
    {
        _client = client;
        _store = store;
        _access = access;
        
        AccessMask am = (access == FileAccess.Read) ? AccessMask.GENERIC_READ : AccessMask.GENERIC_WRITE;
        CreateDisposition cd = (access == FileAccess.Read) ? CreateDisposition.FILE_OPEN : CreateDisposition.FILE_OVERWRITE_IF;

        var status = _store.CreateFile(out _handle, out _, path, am, SMBLibrary.FileAttributes.Normal, ShareAccess.None, cd, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        
        if (status != NTStatus.STATUS_SUCCESS) throw new FileNotFoundException($"SMB CreateFile failed ({access}): " + status);
        
        if (access == FileAccess.Read)
        {
            _store.GetFileInformation(out FileInformation result, _handle, FileInformationClass.FileStandardInformation);
            _length = ((FileStandardInformation)result).EndOfFile; 
        }
        _position = 0;
    }

    public override bool CanRead => _access == FileAccess.Read;
    public override bool CanSeek => true;
    public override bool CanWrite => _access == FileAccess.Write;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
         // ReadFile signature: NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount); 
         // Check library version? Or ulong? Diagnostic tool worked? 
         // Most libraries use long or ulong.
         // Diagnostic tool didn't use ReadFile. 
         // Error says: cannot convert from 'ulong' to 'long'.
         // So signature expects LONG.
         var status = _store.ReadFile(out byte[] data, _handle, (long)_position, (int)count);
         if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_END_OF_FILE) 
             throw new IOException("SMB Read failed: " + status);
             
         if (data == null || data.Length == 0) return 0;
         
         Array.Copy(data, 0, buffer, offset, data.Length);
         _position += data.Length;
         return data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: _position = offset; break;
            case SeekOrigin.Current: _position += offset; break;
            case SeekOrigin.End: _position = _length + offset; break;
        }
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    
    public override void Write(byte[] buffer, int offset, int count) 
    {
        if (_access != FileAccess.Write) throw new NotSupportedException("Stream is ReadOnly");

        // Prepare buffer slice if offset > 0
        byte[] dataToWrite = buffer;
        if (offset != 0 || count != buffer.Length)
        {
             dataToWrite = new byte[count];
             Array.Copy(buffer, offset, dataToWrite, 0, count);
        }

        var status = _store.WriteFile(out int written, _handle, _position, dataToWrite);
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException("SMB Write failed: " + status);

        _position += written;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_handle != null)
        {
            _store.CloseFile(_handle);
            _handle = null;
        }
        _client.Disconnect();
    }
}
