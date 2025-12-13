#!/usr/bin/env python3
import sqlite3
import sys
import os

def upgrade_db(db_path):
    """
    Upgrades WebMusic database to include all sharing features.
    Target Schema: v1.1
    Added Columns:
      - Type (TEXT, default 'normal')
      - ShareToken (TEXT)
      - ShareExpiresAt (TEXT)
      - SharePassword (TEXT)
    """
    if not os.path.exists(db_path):
        print(f"❌ Database not found: {db_path}")
        # Try to find it in common locations relative to script
        common_paths = [
            "../backend/webmusic.db",
            "../data/webmusic.db", 
            "backend/webmusic.db",
            "data/webmusic.db"
        ]
        for p in common_paths:
            if os.path.exists(p):
                print(f"💡 Found database at: {p}")
                db_path = p
                break
        else:
            print("Please provide the correct path as an argument.")
            return

    print(f"📂 Opening database: {db_path}")
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    try:
        # Get current columns
        cursor.execute("PRAGMA table_info(Playlists);")
        columns = {row[1] for row in cursor.fetchall()}
        print(f"📊 Current Playlists columns: {sorted(list(columns))}")
        
        changes = []
        
        # 1. Type
        if 'Type' not in columns:
            print("➕ Adding column: Type")
            cursor.execute("ALTER TABLE Playlists ADD COLUMN Type TEXT DEFAULT 'normal';")
            changes.append("Type")
        
        # 2. ShareToken
        if 'ShareToken' not in columns:
            print("➕ Adding column: ShareToken")
            cursor.execute("ALTER TABLE Playlists ADD COLUMN ShareToken TEXT;")
            changes.append("ShareToken")
            
        # 3. ShareExpiresAt
        if 'ShareExpiresAt' not in columns:
            print("➕ Adding column: ShareExpiresAt")
            cursor.execute("ALTER TABLE Playlists ADD COLUMN ShareExpiresAt TEXT;")
            changes.append("ShareExpiresAt")
            
        # 4. SharePassword
        if 'SharePassword' not in columns:
            print("➕ Adding column: SharePassword")
            cursor.execute("ALTER TABLE Playlists ADD COLUMN SharePassword TEXT;")
            changes.append("SharePassword")
        
        if changes:
            conn.commit()
            print(f"✅ Upgrade successful! Added: {', '.join(changes)}")
        else:
            print("✅ Database is already up to date.")
            
    except Exception as e:
        print(f"❌ Error during upgrade: {e}")
        conn.rollback()
    finally:
        conn.close()

if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else "backend/webmusic.db"
    upgrade_db(path)
