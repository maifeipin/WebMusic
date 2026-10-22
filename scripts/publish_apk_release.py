#!/usr/bin/env python3
"""把构建好的 APK 发布到后端发布目录，并更新 manifest.json。

用法示例：
    # 发布调试渠道（安装包内 applicationId 带 .debug 后缀）
    ./scripts/publish_apk_release.py \
        --apk build_apk/app-debug.apk \
        --channel debug \
        --version-code 2 \
        --version-name 1.1.0 \
        --changelog "- 新增应用内一键检查更新与安装" \
        --package-name com.maifeipin.webmusic.debug

    # 查看将要写入的清单但不落盘
    ./scripts/publish_apk_release.py --apk a.apk --channel debug \
        --version-code 2 --version-name 1.1.0 --dry-run

发布后的目录结构（后端 AppReleaseStore 读取）：

    data/app-releases/
        manifest.json
        debug/webmusic-debug-2.apk

清单被替换后服务端会按文件写入时间自动重载，无需重启容器。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path

CHANNEL_RE = re.compile(r"^[a-z0-9_-]{1,32}$")
DEFAULT_PACKAGE = {
    "debug": "com.maifeipin.webmusic.debug",
    "release": "com.maifeipin.webmusic",
}
REPO_ROOT = Path(__file__).resolve().parent.parent


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def human_size(num_bytes: int) -> str:
    size = float(num_bytes)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} GB"


def load_manifest(path: Path) -> dict:
    if not path.exists():
        return {"channels": {}}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        sys.exit(f"[错误] manifest.json 解析失败: {exc}")
    if not isinstance(data, dict):
        sys.exit("[错误] manifest.json 根节点必须是对象")
    channels = data.get("channels")
    if not isinstance(channels, dict):
        data["channels"] = {}
    return data


def write_manifest_atomic(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="发布 Android APK 到 WebMusic 后端（应用内一键更新的分发源）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("--apk", required=True, help="APK 文件路径")
    parser.add_argument("--channel", default="debug", help="渠道名（debug / release），默认 debug")
    parser.add_argument("--version-code", type=int, required=True, help="versionCode（整数，必须大于旧版）")
    parser.add_argument("--version-name", required=True, help="versionName（展示给用户）")
    parser.add_argument("--changelog", default="", help="更新说明，建议以 '- ' 分行")
    parser.add_argument("--changelog-file", help="从文件读取更新说明（优先于 --changelog）")
    parser.add_argument("--package-name", help="APK 的 applicationId，默认按渠道推断")
    parser.add_argument("--force-update", action="store_true", help="标记为强制更新")
    parser.add_argument("--min-supported-version-code", type=int, help="低于该版本的客户端强制更新")
    parser.add_argument("--root", default=str(REPO_ROOT / "data" / "app-releases"), help="发布根目录")
    parser.add_argument("--keep-old", action="store_true", help="保留旧版本 APK（默认清理同渠道旧包）")
    parser.add_argument("--allow-downgrade", action="store_true", help="允许发布比清单更旧的 versionCode")
    parser.add_argument("--dry-run", action="store_true", help="只打印结果，不写文件")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    channel = args.channel.strip().lower()
    if not CHANNEL_RE.match(channel):
        sys.exit(f"[错误] 渠道名非法: {args.channel!r}（仅允许 a-z0-9_-，1~32 字符）")

    apk = Path(args.apk).expanduser().resolve()
    if not apk.is_file():
        sys.exit(f"[错误] APK 不存在: {apk}")
    if apk.suffix.lower() != ".apk":
        sys.exit(f"[错误] 文件扩展名必须是 .apk: {apk.name}")
    if args.version_code <= 0:
        sys.exit("[错误] --version-code 必须大于 0")

    root = Path(args.root).expanduser().resolve()
    channel_dir = root / channel
    manifest_path = root / "manifest.json"

    manifest = load_manifest(manifest_path)
    channels = manifest["channels"]
    previous = channels.get(channel)

    if previous and isinstance(previous, dict):
        prev_code = int(previous.get("versionCode") or 0)
        if args.version_code < prev_code and not args.allow_downgrade:
            sys.exit(
                f"[错误] 清单中 {channel} 已是 versionCode={prev_code}，"
                f"拒绝发布更旧的 {args.version_code}（如确需回滚请加 --allow-downgrade）"
            )

    target_name = f"webmusic-{channel}-{args.version_code}.apk"
    target = channel_dir / target_name

    changelog = args.changelog
    if args.changelog_file:
        changelog = Path(args.changelog_file).expanduser().read_text(encoding="utf-8")
    changelog = changelog.strip()

    package_name = args.package_name or DEFAULT_PACKAGE.get(channel) or "com.maifeipin.webmusic"
    digest = sha256_of(apk)
    size = apk.stat().st_size
    published_at = datetime.now(timezone(timedelta(hours=8))).isoformat(timespec="seconds")

    entry = {
        "versionCode": args.version_code,
        "versionName": args.version_name.strip(),
        "fileName": target_name,
        "packageName": package_name.strip(),
        "changelog": changelog,
        "sha256": digest,
        "forceUpdate": bool(args.force_update),
        "minSupportedVersionCode": args.min_supported_version_code,
        "publishedAt": published_at,
    }
    channels[channel] = entry

    print(f"渠道        : {channel}")
    print(f"版本        : {entry['versionName']} (versionCode={entry['versionCode']})")
    print(f"包名        : {entry['packageName']}")
    print(f"大小        : {human_size(size)}")
    print(f"SHA-256     : {digest}")
    print(f"强制更新    : {entry['forceUpdate']}")
    print(f"目标文件    : {target}")
    print(f"清单文件    : {manifest_path}")

    if args.dry_run:
        print("\n[dry-run] 未写入任何文件。清单内容预览：")
        print(json.dumps({"channels": channels}, ensure_ascii=False, indent=2))
        return 0

    channel_dir.mkdir(parents=True, exist_ok=True)
    if apk != target:
        shutil.copyfile(apk, target)
        print(f"\n已复制 APK -> {target}")

    # 校验落盘副本与源文件一致，避免半截文件对外分发
    if target.stat().st_size != size or sha256_of(target) != digest:
        target.unlink(missing_ok=True)
        sys.exit("[错误] 复制后的 APK 校验不一致，已删除，请重试")

    if not args.keep_old:
        for stale in channel_dir.glob("*.apk"):
            if stale.name != target_name:
                stale.unlink()
                print(f"已清理旧版本: {stale.name}")

    write_manifest_atomic(manifest_path, manifest)
    print(f"已更新清单: {manifest_path}")

    print("\n验证命令：")
    print(f"  curl -s 'http://localhost:5080/api/app/version?currentCode=1&channel={channel}'")
    print(f"  curl -sI 'http://localhost:5080/api/app/download/{channel}'")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
