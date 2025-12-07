import { useState, useEffect } from 'react';
import { getDirectory } from '../services/api';
import { ChevronRight, ChevronDown, Folder, Music, Play } from 'lucide-react';

export interface DirectoryItem {
    type: 'Folder' | 'File';
    id: number;
    name: string;
    path?: string; // Full Path for navigation
    artist?: string;
    album?: string;
    count?: number;
}

interface DirectoryTreeProps {
    onPlayFile: (id: number, title: string, artist: string, album: string) => void;
    onPlayFolder: (path: string) => void;
}

export default function DirectoryTree({ onPlayFile, onPlayFolder }: DirectoryTreeProps) {
    const [rootItems, setRootItems] = useState<DirectoryItem[]>([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        loadRoot();
    }, []);

    const loadRoot = async () => {
        try {
            const res = await getDirectory("");
            setRootItems(res.data);
        } catch (e) {
            console.error(e);
            setLoading(false);
        } finally {
            setLoading(false);
        }
    };

    if (loading) return <div className="text-gray-500 p-4">Loading directory structure...</div>;

    return (
        <div className="select-none text-sm">
            {rootItems.map((item, i) => (
                <TreeNode
                    key={item.name + i}
                    item={item}
                    onPlayFile={onPlayFile}
                    onPlayFolder={onPlayFolder}
                />
            ))}
            {rootItems.length === 0 && <div className="text-gray-500">No items found.</div>}
        </div>
    );
}

interface TreeNodeProps {
    item: DirectoryItem;
    onPlayFile: (id: number, title: string, artist: string, album: string) => void;
    onPlayFolder: (path: string) => void;
}

function TreeNode({ item, onPlayFile, onPlayFolder }: TreeNodeProps) {
    const [expanded, setExpanded] = useState(false);
    const [children, setChildren] = useState<DirectoryItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [hasLoaded, setHasLoaded] = useState(false);

    // Use Backend provided Path, or fallback to name (should rely on Path)
    const currentPath = item.path || item.name;

    const handleExpand = async (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type === 'File') return;

        if (!expanded && !hasLoaded) {
            setLoading(true);
            try {
                const res = await getDirectory(currentPath);
                setChildren(res.data);
                setHasLoaded(true);
            } catch (error) {
                console.error("Failed to load directory", error);
            } finally {
                setLoading(false);
            }
        }
        setExpanded(!expanded);
    };

    const handlePlayClick = (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type === 'Folder') {
            onPlayFolder(currentPath);
        } else {
            onPlayFile(item.id, item.name, item.artist || '', item.album || '');
        }
    };

    return (
        <div className="pl-4">
            <div
                className={`group flex items-center gap-2 py-1 px-2 rounded cursor-pointer transition ${item.type === 'Folder' ? 'hover:bg-gray-800 text-gray-200' : 'hover:bg-gray-800/50 text-gray-400'
                    }`}
                onClick={(e) => {
                    if (item.type === 'File') handlePlayClick(e);
                    else handleExpand(e);
                }}
            >
                {/* Icon & Expander */}
                <div className="flex items-center gap-1 min-w-[24px]">
                    {item.type === 'Folder' && (
                        <button onClick={handleExpand} className="p-0.5 hover:text-white">
                            {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                        </button>
                    )}
                </div>

                {/* Type Icon */}
                {item.type === 'Folder' ? (
                    <Folder size={16} className="text-blue-500" fill="currentColor" fillOpacity={0.2} />
                ) : (
                    <Music size={16} />
                )}

                {/* Name & Count */}
                <span className="flex-1 truncate flex items-center gap-2">
                    <span>{item.name}</span>
                    {item.type === 'Folder' && item.count !== undefined && (
                        <span className="text-xs text-gray-600">({item.count})</span>
                    )}
                    {item.type === 'File' && item.artist && <span className="text-gray-600">- {item.artist}</span>}
                </span>

                {/* Play Button (Hover, always visible for Folder) */}
                <button
                    onClick={handlePlayClick}
                    className={`p-1 hover:text-blue-400 text-gray-500 transition-opacity ${item.type === 'Folder' ? '' : 'opacity-0 group-hover:opacity-100'}`}
                    title={item.type === 'Folder' ? "Play All in Folder" : "Play Song"}
                >
                    <Play size={14} fill="currentColor" />
                </button>
            </div>

            {/* Children */}
            {expanded && item.type === 'Folder' && (
                <div className="border-l border-gray-800 ml-3">
                    {loading && <div className="pl-6 py-1 text-xs text-gray-600">Loading...</div>}
                    {!loading && children.map((child, i) => (
                        <TreeNode
                            key={child.name + i}
                            item={child}
                            onPlayFile={onPlayFile}
                            onPlayFolder={onPlayFolder}
                        />
                    ))}
                    {!loading && children.length === 0 && (
                        <div className="pl-6 py-1 text-xs text-gray-600">Empty</div>
                    )}
                </div>
            )}
        </div>
    );
}
