import psycopg2
import os
import sys

# Load env vars
script_dir = os.path.dirname(os.path.abspath(__file__))
project_root = os.path.dirname(os.path.dirname(script_dir)) # Test/migrations -> Test -> v2
env_file = os.path.join(project_root, ".env")

if os.path.exists(env_file):
    print(f"Loading env from: {env_file}")
    with open(env_file) as f:
        for line in f:
            if line.strip() and not line.startswith('#'):
                parts = line.strip().split('=', 1)
                if len(parts) == 2:
                    key, val = parts
                    os.environ[key] = val
                    # Debug sensitive keys by hiding value
                    if "PASSWORD" in key or "KEY" in key:
                        print(f"  Loaded {key}=***")
                    else:
                        print(f"  Loaded {key}={val}")

else:
    print(f"File not found: {env_file}")
    sys.exit(1)

if os.path.exists(env_file):
    with open(env_file) as f:
        for line in f:
            if line.strip() and not line.startswith('#'):
                key, val = line.strip().split('=', 1)
                os.environ[key] = val

def upgrade_schema():
    host = os.environ.get("POSTGRES_HOST", "postgres")
    user = os.environ.get("POSTGRES_USER", "postgres")
    password = os.environ.get("POSTGRES_PASSWORD", "password")
    dbname = os.environ.get("POSTGRES_DB", "webmusic")

    print(f"Connecting to {dbname} on {host}...")
    
    try:
        conn = psycopg2.connect(host=host, database=dbname, user=user, password=password)
        cur = conn.cursor()

            print("Checking/Creating table 'Lyrics'...")
            # Matches C# Entity definition
            # Use IF NOT EXISTS to be safe
            cur.execute("""
                CREATE TABLE IF NOT EXISTS "Lyrics" (
                    "Id" SERIAL PRIMARY KEY,
                    "MediaFileId" INTEGER NOT NULL REFERENCES "MediaFiles"("Id") ON DELETE CASCADE,
                    "Content" TEXT,
                    "Language" TEXT DEFAULT 'unknown',
                    "Source" TEXT DEFAULT 'AI',
                    "Version" TEXT DEFAULT 'v1',
                    "CreatedAt" TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            """)
            print("Table 'Lyrics' ensured.")

            print("Ensuring Index on MediaFileId...")
            cur.execute("""
                CREATE INDEX IF NOT EXISTS "IX_Lyrics_MediaFileId" ON "Lyrics" ("MediaFileId");
            """)
            print("Index ensured.")

        conn.commit()
        cur.close()
        conn.close()

    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    upgrade_schema()
