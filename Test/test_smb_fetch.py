import os
import json
import psycopg2
from dotenv import load_dotenv
import uuid
import sys

# SMB Imports
from smbprotocol.connection import Connection
from smbprotocol.session import Session
from smbprotocol.tree import TreeConnect
from smbprotocol.open import Open, CreateDisposition, FilePipePrinterAccessMask, FileAttributes, ShareAccess, CreateOptions, ImpersonationLevel

# Load environment variables
load_dotenv(os.path.join(os.path.dirname(__file__), '../.env'))

DB_HOST = os.getenv("POSTGRES_HOST", "localhost")
DB_USER = os.getenv("POSTGRES_USER", "postgres")
DB_PASS = os.getenv("POSTGRES_PASSWORD", "password")
DB_NAME = os.getenv("POSTGRES_DB", "webmusic")
DB_PORT = os.getenv("POSTGRES_PORT", "5432")

def get_media_info():
    try:
        conn = psycopg2.connect(
            host=DB_HOST,
            user=DB_USER,
            password=DB_PASS,
            dbname=DB_NAME,
            port=DB_PORT
        )
        cur = conn.cursor()
        
        # Query for a media file that has a ScanSource
        # We join MediaFiles (m) with ScanSources (s)
        # Note: Table names might be pluralized nicely by EF Core ("MediaFiles", "ScanSources")
        query = """
            SELECT 
                m."Id", 
                m."FilePath", 
                s."Path" as SourcePath,
                s."StorageCredentialId"
            FROM "MediaFiles" m
            JOIN "ScanSources" s ON m."ScanSourceId" = s."Id"
            LIMIT 1;
        """
        
        cur.execute(query)
        row = cur.fetchone()
        
        if not row:
            print("No media files found with associated ScanSource.")
            return None
            
        media_id, file_path, source_path, cred_id = row
        print(f"Found Media ID: {media_id}")
        print(f"File Path: {file_path}")
        print(f"Source Path: {source_path}")
        
        # Now get Credentials
        if cred_id:
            cur.execute('SELECT "AuthData", "Host" FROM "StorageCredentials" WHERE "Id" = %s', (cred_id,))
            cred_row = cur.fetchone()
            if cred_row:
                auth_data_json, cred_host = cred_row
                auth_data = json.loads(auth_data_json)
                username = auth_data.get("username", "")
                password = auth_data.get("password", "")
                
                # Close DB
                cur.close()
                conn.close()
                
                return {
                    "file_path": file_path,
                    "source_path": source_path,
                    "cred_host": cred_host,
                    "username": username,
                    "password": password
                }
        
        cur.close()
        conn.close()
        return None

    except Exception as e:
        print(f"Database Error: {e}")
        return None

def download_file(info):
    host = info['cred_host']
    if host.startswith("smb://"):
        host = host.replace("smb://", "").split("/")[0]
        
    username = info['username']
    password = info['password']
    
    # Determine Share
    # Logic from C#: if source_path starts with smb://, segments[1] is share
    share = ""
    if info['source_path'].startswith("smb://"):
        import urllib.parse
        p = urllib.parse.urlparse(info['source_path'])
        path_parts = p.path.strip('/').split('/')
        if path_parts:
            share = path_parts[0]
            
    if not share:
        # Fallback simple split logic
        parts = info['source_path'].replace('\\', '/').split('/')
        if len(parts) > 0 and not info['source_path'].startswith("smb://"):
            share = parts[0]
            
    if not share:
        print("Could not determine Share Name from Source Path.")
        return

    # Check File Path format
    # If file_path is "sharedata/..." and share is "DataSync", we trust it works relative to Share?
    # Or needs directory traversal?
    target_path = info['file_path'].replace('/', '\\')
    
    print(f"\nAttempting SMB Connection:")
    print(f"Host: {host}")
    print(f"Share: {share}")
    print(f"User: {username}")
    print(f"File: {target_path}")
    
    conn = Connection(uuid.uuid4(), host, 445)
    try:
        print("Connecting...")
        conn.connect()
        session = Session(conn, username, password)
        session.connect()
        print("Session Connected.")
        
        tree = TreeConnect(session, f"\\\\{host}\\{share}")
        tree.connect()
        print(f"Tree Connected to {share}.")
        
        print("Opening File...")
        file_open = Open(tree, target_path)
        file_open.create(ImpersonationLevel.Impersonation,
                         FilePipePrinterAccessMask.GENERIC_READ, 
                         FileAttributes.FILE_ATTRIBUTE_NORMAL, 
                         ShareAccess.FILE_SHARE_READ,
                         CreateDisposition.FILE_OPEN, 
                         CreateOptions.FILE_NON_DIRECTORY_FILE)
        
        print("Reading first 1KB...")
        data = file_open.read(0, 1024)
        print(f"Read Success! Got {len(data)} bytes.")
        
        file_open.close()
        
    except Exception as e:
        print(f"SMB Error: {e}")
    finally:
        if conn:
            conn.disconnect()

if __name__ == "__main__":
    print("=== Testing SMB Fetch from DB Info ===")
    info = get_media_info()
    if info:
        download_file(info)
    else:
        print("Could not retrieve media info.")
