import { useState, useEffect } from 'react';
import { getDirectory } from '../services/api';
import { ChevronRight, ChevronDown, Folder, Music, Play, Square, CheckSquare } from 'lucide-react';

export interface DirectoryItem {
    type: 'Folder' | 'File';
    id: number;
    name: string;
    path?: string; // Full Path for navigation
    artist?: string;
    album?: string;
    count?: number; // Number of songs in folder
}

interface DirectoryTreeProps {
    onPlayFile: (id: number, title: string, artist: string, album: string) => void;
    onPlayFolder: (path: string) => void;
    // Selection Props
    selectedPaths: string[];
    selectedFileIds: number[];
    onTogglePath: (path: string, count?: number) => void;
    onToggleFile: (id: number) => void;
}

export default function DirectoryTree({
    onPlayFile,
    onPlayFolder,
    selectedPaths,
    selectedFileIds,
    onTogglePath,
    onToggleFile
}: DirectoryTreeProps) {
    const [rootItems, setRootItems] = useState<DirectoryItem[]>([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        loadRoot();
    }, []);

    const loadRoot = async () => {
        try {
            setLoading(true);
            const res = await getDirectory("");
            setRootItems(res.data);
        } catch (e) {
            console.error(e);
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="select-none text-sm">
            {loading && <div className="p-2 text-gray-500">Loading...</div>}
            {!loading && rootItems.map((item, i) => (
                <TreeNode
                    key={item.name + i}
                    item={item}
                    onPlayFile={onPlayFile}
                    onPlayFolder={onPlayFolder}
                    selectedPaths={selectedPaths}
                    selectedFileIds={selectedFileIds}
                    onTogglePath={onTogglePath}
                    onToggleFile={onToggleFile}
                />
            ))}
            {!loading && rootItems.length === 0 && <div className="text-gray-500">No items found.</div>}
        </div>
    );
}

interface TreeNodeProps {
    item: DirectoryItem;
    onPlayFile: (id: number, title: string, artist: string, album: string) => void;
    onPlayFolder: (path: string) => void;
    selectedPaths: string[];
    selectedFileIds: number[];
    onTogglePath: (path: string, count?: number) => void;
    onToggleFile: (id: number) => void;
}

function TreeNode({
    item,
    onPlayFile,
    onPlayFolder,
    selectedPaths,
    selectedFileIds,
    onTogglePath,
    onToggleFile
}: TreeNodeProps) {
    const [expanded, setExpanded] = useState(false);
    const [children, setChildren] = useState<DirectoryItem[]>([]);
    const [loading, setLoading] = useState(false);

    // Use Backend provided Path, or fallback to name
    const currentPath = item.path || item.name;

    // Selection State
    const isFolderSelected = item.type === 'Folder' && selectedPaths.includes(currentPath);
    const isFileSelected = item.type === 'File' && selectedFileIds.includes(item.id);

    const handleExpand = async (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type !== 'Folder') return;

        const nextState = !expanded;
        setExpanded(nextState);

        if (nextState && children.length === 0) {
            try {
                setLoading(true);
                const res = await getDirectory(currentPath);
                setChildren(res.data);
            } catch (e) {
                console.error(e);
            } finally {
                setLoading(false);
            }
        }
    };

    const handlePlayClick = (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type === 'File') {
            onPlayFile(item.id, item.name, item.artist || '', item.album || '');
        } else {
            onPlayFolder(currentPath);
        }
    };

    const handleToggle = (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type === 'Folder') {
            onTogglePath(currentPath, item.count);
        } else {
            onToggleFile(item.id);
        }
    };

    return (
        <div className="pl-4">
            <div
                className={`group flex items-center gap-2 py-1 px-2 rounded cursor-pointer transition ${isFolderSelected || isFileSelected ? 'bg-blue-900/30' : 'hover:bg-gray-800'
                    }`}
                onClick={handleExpand}
                onDoubleClick={(e) => {
                    if (item.type === 'File') handlePlayClick(e);
                    else handleExpand(e);
                }}
            >
                {/* Checkbox */}
                <button
                    onClick={handleToggle}
                    className={`p-0.5 rounded transition ${isFolderSelected || isFileSelected ? 'text-blue-500' : 'text-gray-600 hover:text-gray-400'
                        }`}
                >
                    {(isFolderSelected || isFileSelected) ? <CheckSquare size={16} /> : <Square size={16} />}
                </button>

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
                    <Folder size={16} className={isFolderSelected ? 'text-blue-400' : 'text-yellow-500'} />
                ) : (
                    <Music size={16} className={isFileSelected ? 'text-blue-400' : 'text-gray-400'} />
                )}

                {/* Name */}
                <div className="flex-1 truncate">
                    <span className={isFolderSelected || isFileSelected ? 'text-blue-100' : 'text-gray-300'}>
                        {item.name}
                    </span>
                    {item.count !== undefined && item.type === 'Folder' && (
                        <span className="ml-2 text-xs text-gray-500">({item.count})</span>
                    )}
                </div>

                {/* Play Button (Hover) */}
                <button
                    onClick={handlePlayClick}
                    className="opacity-0 group-hover:opacity-100 p-1 hover:bg-white/10 rounded text-gray-300 hover:text-white transition"
                    title="Play"
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
                            selectedPaths={selectedPaths}
                            selectedFileIds={selectedFileIds}
                            onTogglePath={onTogglePath}
                            onToggleFile={onToggleFile}
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
