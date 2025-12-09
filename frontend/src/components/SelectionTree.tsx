import { useState, useEffect } from 'react';
import { getDirectory } from '../services/api';
import { ChevronRight, ChevronDown, Folder, CheckSquare, Square } from 'lucide-react';

export interface DirectoryItem {
    type: 'Folder' | 'File';
    id: number;
    name: string;
    path?: string;
    artist?: string;
    album?: string;
    count?: number;
}

interface SelectionTreeProps {
    onNameSuggest: (name: string) => void;
    selectedIds: number[];
    onToggleSelect: (id: number) => void;
}

export default function SelectionTree({ onNameSuggest, selectedIds, onToggleSelect }: SelectionTreeProps) {
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
        } finally {
            setLoading(false);
        }
    };

    if (loading) return <div className="text-gray-500 p-4">Loading...</div>;

    return (
        <div className="select-none text-sm h-full overflow-y-auto">
            {rootItems.map((item, i) => (
                <TreeNode
                    key={item.name + i}
                    item={item}
                    onNameSuggest={onNameSuggest}
                    selectedIds={selectedIds}
                    onToggleSelect={onToggleSelect}
                />
            ))}
        </div>
    );
}

interface TreeNodeProps {
    item: DirectoryItem;
    onNameSuggest: (name: string) => void;
    selectedIds: number[];
    onToggleSelect: (id: number) => void;
}

function TreeNode({ item, onNameSuggest, selectedIds, onToggleSelect }: TreeNodeProps) {
    const [expanded, setExpanded] = useState(false);
    const [children, setChildren] = useState<DirectoryItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [hasLoaded, setHasLoaded] = useState(false);

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

    const handleFolderClick = (e: React.MouseEvent) => {
        e.stopPropagation();
        if (item.type === 'Folder') {
            onNameSuggest(item.name);
            handleExpand(e);
        }
    };

    const isSelected = item.type === 'File' && selectedIds.includes(item.id);

    return (
        <div className="pl-4">
            <div
                className={`group flex items-center gap-2 py-1 px-2 rounded cursor-pointer transition ${item.type === 'Folder' ? 'hover:bg-gray-800 text-gray-200' : 'hover:bg-gray-800/50 text-gray-400'
                    }`}
                onClick={(e) => {
                    if (item.type === 'Folder') handleFolderClick(e);
                    else onToggleSelect(item.id);
                }}
            >
                {/* Expander */}
                <div className="flex items-center gap-1 min-w-[20px]">
                    {item.type === 'Folder' && (
                        <button onClick={handleExpand} className="p-0.5 hover:text-white">
                            {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                        </button>
                    )}
                </div>

                {/* Checkbox for File */}
                {item.type === 'File' && (
                    <div className={isSelected ? "text-blue-500" : "text-gray-600"}>
                        {isSelected ? <CheckSquare size={16} /> : <Square size={16} />}
                    </div>
                )}

                {/* Icon */}
                {item.type === 'Folder' ? (
                    <Folder size={16} className="text-blue-500" fill="currentColor" fillOpacity={0.2} />
                ) : null}

                {/* Name */}
                <span className="flex-1 truncate flex items-center gap-2">
                    <span>{item.name}</span>
                    {item.type === 'File' && item.artist && <span className="text-gray-600">- {item.artist}</span>}
                </span>
            </div>

            {/* Children */}
            {expanded && item.type === 'Folder' && (
                <div className="border-l border-gray-800 ml-3">
                    {loading && <div className="pl-6 py-1 text-xs text-gray-600">Loading...</div>}
                    {!loading && children.map((child, i) => (
                        <TreeNode
                            key={child.name + i}
                            item={child}
                            onNameSuggest={onNameSuggest}
                            selectedIds={selectedIds}
                            onToggleSelect={onToggleSelect}
                        />
                    ))}
                </div>
            )}
        </div>
    );
}
