import React, { useState, useEffect, useRef } from 'react';
import { browseFiles, createDirectory, uploadFile, type FileItem } from '../services/files';

interface FileManagerProps {
    onClose: () => void;
}

export const FileManager: React.FC<FileManagerProps> = ({ onClose }) => {
    const [currentPath, setCurrentPath] = useState('');
    const [currentSourceId, setCurrentSourceId] = useState<number | undefined>(undefined);
    const [items, setItems] = useState<FileItem[]>([]);
    const [loading, setLoading] = useState(false);
    const [uploadProgress, setUploadProgress] = useState<number | null>(null);
    const [showNewFolderInput, setShowNewFolderInput] = useState(false);
    const [newFolderName, setNewFolderName] = useState('');

    // History Stack for Back Nav
    const [history, setHistory] = useState<{ sid: number | undefined, path: string }[]>([]);

    const loadRef = useRef(false);

    const loadFiles = async (sid?: number, path: string = '') => {
        setLoading(true);
        try {
            const data = await browseFiles(sid, path);
            setItems(data);
            setCurrentSourceId(sid);
            setCurrentPath(path);
        } catch (err) {
            console.error(err);
            alert("Failed to load files");
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        if (!loadRef.current) {
            loadFiles(undefined, '');
            loadRef.current = true;
        }
    }, []);

    const handleNavigate = (item: FileItem) => {
        if (item.type === 'File') return; // Selection? Download?

        // Push current to history
        setHistory([...history, { sid: currentSourceId, path: currentPath }]);

        if (item.type === 'Source') {
            loadFiles(item.sourceId, '');
        } else {
            // Directory
            // Item path is relative or full? 
            // In Controller we return relative path to browse root.
            loadFiles(item.sourceId, item.path); // path is computed in backend
        }
    };

    const handleBack = () => {
        if (history.length === 0) return;
        const previous = history[history.length - 1];
        setHistory(history.slice(0, -1));
        loadFiles(previous.sid, previous.path);
    };

    const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
        if (!e.target.files?.length || !currentSourceId) return;

        const file = e.target.files[0];
        setUploadProgress(0);

        try {
            await uploadFile(currentSourceId, currentPath, file, (pct) => setUploadProgress(pct));
            alert("Upload Complete!");
            loadFiles(currentSourceId, currentPath); // Refresh
        } catch (err) {
            console.error(err);
            alert("Upload Failed");
        } finally {
            setUploadProgress(null);
        }
    };

    const handleCreateFolder = async () => {
        if (!newFolderName || !currentSourceId) return;

        try {
            // Path sent to mkdir should be relative to source base?
            // currentPath is relative path. newFolder is sub.
            const newPath = currentPath ? `${currentPath}/${newFolderName}` : newFolderName;
            await createDirectory(currentSourceId, newPath);
            setNewFolderName('');
            setShowNewFolderInput(false);
            loadFiles(currentSourceId, currentPath);
        } catch (err) {
            alert("Failed to create folder");
        }
    };

    return (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center z-50 p-4">
            <div className="bg-gray-800 rounded-xl w-full max-w-4xl h-[80vh] flex flex-col shadow-2xl overflow-hidden border border-white/10">
                {/* Toolbar */}
                <div className="p-4 bg-gray-900 border-b border-white/10 flex justify-between items-center">
                    <div className="flex items-center gap-3">
                        <button onClick={handleBack} disabled={history.length === 0} className="p-2 hover:bg-white/10 rounded disabled:opacity-30">
                            ⬅
                        </button>
                        <h2 className="text-lg font-bold truncate max-w-md">
                            {currentSourceId ? (currentPath || '/') : 'Home'}
                        </h2>
                    </div>
                    <div className="flex gap-2">
                        {currentSourceId && (
                            <>
                                <button
                                    onClick={() => setShowNewFolderInput(!showNewFolderInput)}
                                    className="px-3 py-1.5 bg-gray-700 hover:bg-gray-600 rounded text-sm transition"
                                >
                                    + Folder
                                </button>
                                <label className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 rounded text-sm cursor-pointer transition flex items-center gap-2">
                                    <span>Upload</span>
                                    <input type="file" className="hidden" onChange={handleUpload} />
                                </label>
                            </>
                        )}
                        <button onClick={onClose} className="px-3 py-1.5 bg-red-500/20 text-red-500 hover:bg-red-500 hover:text-white rounded text-sm transition">
                            Close
                        </button>
                    </div>
                </div>

                {/* Progress Bar */}
                {uploadProgress !== null && (
                    <div className="h-1 bg-gray-700 w-full">
                        <div className="h-full bg-indigo-500 transition-all duration-300" style={{ width: `${uploadProgress}%` }} />
                    </div>
                )}

                {/* New Folder Input */}
                {showNewFolderInput && (
                    <div className="p-2 bg-gray-700 flex gap-2 animate-fade-in">
                        <input
                            type="text"
                            className="flex-1 bg-gray-900 border border-gray-600 rounded px-3 py-1 text-sm outline-none focus:border-indigo-500"
                            placeholder="Folder Name"
                            value={newFolderName}
                            onChange={e => setNewFolderName(e.target.value)}
                            autoFocus
                            onKeyDown={e => e.key === 'Enter' && handleCreateFolder()}
                        />
                        <button onClick={handleCreateFolder} className="px-3 py-1 bg-green-600 rounded text-xs">Confirm</button>
                    </div>
                )}

                {/* List */}
                <div className="flex-1 overflow-y-auto p-4">
                    {loading ? (
                        <div className="text-center text-gray-400 py-10">Loading...</div>
                    ) : (
                        <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
                            {items.map((item, idx) => (
                                <div
                                    key={idx}
                                    onClick={() => handleNavigate(item)}
                                    className={`
                                        group relative p-4 bg-gray-700/30 rounded-lg hover:bg-gray-700/80 cursor-pointer transition border border-transparent hover:border-white/10 flex flex-col items-center gap-3 text-center
                                        ${item.type === 'File' ? 'opacity-75 hover:opacity-100' : ''}
                                    `}
                                >
                                    <div className="text-4xl">
                                        {item.type === 'Source' ? '🖥️' : item.type === 'Directory' ? '📁' : '📄'}
                                    </div>
                                    <span className="text-sm text-gray-300 group-hover:text-white truncate w-full" title={item.name}>
                                        {item.name}
                                    </span>
                                </div>
                            ))}

                            {!loading && items.length === 0 && (
                                <div className="col-span-full text-center text-gray-500 py-10">Empty Folder</div>
                            )}
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
};
