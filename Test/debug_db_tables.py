import psycopg2
import os

# Load env vars
script_dir = os.path.dirname(os.path.abspath(__file__))
project_root = os.path.dirname(os.path.dirname(script_dir))
env_file = os.path.join(project_root, ".env")

if os.path.exists(env_file):
    with open(env_file) as f:
        for line in f:
            if line.strip() and not line.startswith('#'):
                key, val = line.strip().split('=', 1)
                os.environ[key] = val

def list_tables():
    host = os.environ.get("POSTGRES_HOST", "postgres")
    user = os.environ.get("POSTGRES_USER", "postgres")
    password = os.environ.get("POSTGRES_PASSWORD", "password")
    dbname = os.environ.get("POSTGRES_DB", "webmusic")

    print(f"Checking tables on {host} / {dbname} ...")
    
    try:
        conn = psycopg2.connect(host=host, database=dbname, user=user, password=password)
        cur = conn.cursor()
        
        cur.execute("""
            SELECT table_name 
            FROM information_schema.tables 
            WHERE table_schema = 'public'
            ORDER BY table_name;
        """)
        
        rows = cur.fetchall()
        print(f"Found {len(rows)} tables:")
        for row in rows:
            print(f" - {row[0]}")

        conn.close()
    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    list_tables()
