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
        # Check if Plugins table exists
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='Plugins';")
        if cursor.fetchone():
            print("✅ Plugins table already exists.")
        else:
            print("➕ Creating columns for Plugins table...")
            cursor.execute("""
                CREATE TABLE Plugins (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL DEFAULT '',
                    Description TEXT DEFAULT '',
                    BaseUrl TEXT DEFAULT '',
                    EntryPath TEXT DEFAULT '/',
                    Icon TEXT DEFAULT 'Extension',
                    IsEnabled INTEGER DEFAULT 1,
                    CreatedAt TEXT NOT NULL
                );
            """)
            print("✅ Plugins table created successfully.")
            
        conn.commit()

    except Exception as e:
        print(f"❌ Error during upgrade: {e}")
        conn.rollback()
    finally:
        conn.close()

if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else "backend/webmusic.db"
    upgrade_db(path)
