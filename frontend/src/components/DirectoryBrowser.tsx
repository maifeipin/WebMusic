import { useState, useEffect } from 'react';
import { browse } from '../services/api';
import { Folder, Server, ArrowUp, Check, X, Loader } from 'lucide-react';

interface Props {
    credentialId: number;
    initialPath?: string;
    onSelect: (path: string) => void;
    onClose: () => void;
}

interface AvailableItem {
    name: string;
    type: 'Share' | 'Directory' | 'File';
    path: string;
}

export default function DirectoryBrowser({ credentialId, initialPath, onSelect, onClose }: Props) {
    const [currentPath, setCurrentPath] = useState(initialPath || '');
    const [items, setItems] = useState<AvailableItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState('');

    useEffect(() => {
        loadPath(currentPath);
    }, [currentPath]);

    const loadPath = async (path: string) => {
        if (!credentialId) return;
        setLoading(true);
        setError('');
        try {
            const res = await browse({ credentialId, path });
            setItems(res.data);
        } catch (err) {
            console.error(err);
            setError('Failed to load directory. Check credentials or connection.');
        } finally {
            setLoading(false);
        }
    };

    const handleUp = () => {
        if (!currentPath) return;
        // Logic to go up one level
        // Path: Share/Dir/Sub -> Share/Dir -> Share -> ""
        const parts = currentPath.split('/').filter(p => p);
        parts.pop();
        setCurrentPath(parts.join('/'));
    };

    const handleItemClick = (item: AvailableItem) => {
        if (item.type === 'File') return; // Maybe simple browse doesn't select files? Or maybe it does?
        // Navigate
        setCurrentPath(item.path);
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/90 backdrop-blur-sm">
            <div className="bg-gray-900 w-[90vw] max-w-6xl rounded-xl border border-gray-700 shadow-2xl flex flex-col max-h-[85vh] overflow-hidden relative">

                {/* Header */}
                <div className="p-4 border-b border-gray-800 flex items-center justify-between">
                    <h3 className="font-bold text-lg text-white flex items-center gap-2">
                        <Folder className="text-blue-500" /> Remote Browser
                    </h3>
                    <button onClick={onClose} className="text-gray-400 hover:text-white"><X size={20} /></button>
                </div>

                {/* Toolbar */}
                <div className="p-2 gap-2 flex items-center bg-gray-950 border-b border-gray-800 text-sm">
                    <button
                        onClick={handleUp}
                        disabled={!currentPath}
                        className="p-2 hover:bg-gray-800 rounded disabled:opacity-30 disabled:cursor-not-allowed text-gray-300"
                    >
                        <ArrowUp size={18} />
                    </button>
                    <div className="flex-1 px-2 py-1 bg-gray-800 rounded text-gray-300 font-mono truncate">
                        {currentPath || "Root (Shares)"}
                    </div>
                </div>

                {/* List */}
                <div className="flex-1 overflow-y-auto p-2">
                    {loading ? (
                        <div className="flex items-center justify-center h-40 flex-col gap-2 text-gray-500">
                            <Loader className="animate-spin" />
                            <span>Loading...</span>
                        </div>
                    ) : error ? (
                        <div className="text-red-400 p-4 text-center">{error}</div>
                    ) : (
                        <div className="grid gap-3">
                            {items.filter(i => i.type !== 'File').length === 0 && <div className="text-gray-500 p-4">Empty folder</div>}
                            {items.filter(i => i.type !== 'File').map((item, idx) => (
                                <div
                                    key={idx}
                                    onClick={() => handleItemClick(item)}
                                    className={`
                                        flex items-center gap-3 p-4 rounded-lg cursor-pointer transition
                                        text-gray-200 hover:bg-gray-800 hover:ring-1 hover:ring-blue-500/30 hover:text-white
                                        bg-gray-800/20 border border-gray-800/50
                                    `}
                                >
                                    {item.type === 'Share' && <Server size={24} className="text-green-500" />}
                                    {item.type === 'Directory' && <Folder size={24} className="text-yellow-500" />}
                                    <span className="truncate text-lg">{item.name}</span>
                                </div>
                            ))}
                        </div>
                    )}
                </div>

                {/* Footer */}
                <div className="p-4 border-t border-gray-800 flex justify-end gap-2 bg-gray-900 rounded-b-xl">
                    <div className="flex-1 flex items-center">
                        <input
                            type="text"
                            value={currentPath}
                            readOnly
                            className="w-full bg-gray-950 border border-gray-800 rounded px-3 py-2 text-sm text-gray-300 focus:outline-none"
                            placeholder="Selected Path..."
                        />
                    </div>
                    <button onClick={onClose} className="px-4 py-2 text-gray-300 hover:text-white">Cancel</button>
                    <button
                        onClick={() => onSelect(currentPath)}
                        disabled={!currentPath}
                        className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded flex items-center gap-2 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                        <Check size={16} /> Select
                    </button>
                </div>
            </div>
        </div>
    );
}
