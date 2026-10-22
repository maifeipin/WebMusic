#!/usr/bin/env python3
"""Create a complete-APK update feed from the cloud build's exact bytes."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--apk', type=Path, required=True)
parser.add_argument('--code', type=int, required=True)
parser.add_argument('--repo', required=True)
args = parser.parse_args()
out = Path('build_apk')
out.mkdir(exist_ok=True)
name = f'webmusic-debug-{args.code}.apk'
shutil.copyfile(args.apk, out / name)
manifest = dict(hasUpdate=True, channel='debug', versionCode=args.code,
                versionName=f'1.1.{args.code}', packageName='com.maifeipin.webmusic.debug',
                sizeBytes=args.apk.stat().st_size, sha256=hashlib.sha256(args.apk.read_bytes()).hexdigest(),
                downloadUrl=f'https://github.com/{args.repo}/releases/download/android-debug-{args.code}/{name}',
                changelog='完整 APK 更新；安装前保存播放队列与进度。')
(out / 'update.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
