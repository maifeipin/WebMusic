import React from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

interface PaginationProps {
    currentPage: number;
    totalItems: number;
    pageSize: number;
    onPageChange: (page: number) => void;
}

export const Pagination: React.FC<PaginationProps & { onPageSizeChange: (size: number) => void }> = ({ currentPage, totalItems, pageSize, onPageChange, onPageSizeChange }) => {
    const totalPages = Math.ceil(totalItems / pageSize);

    return (
        <div className="flex flex-col md:flex-row items-center justify-between border-t border-gray-800 pt-4 mt-8 gap-4">
            <div className="flex items-center gap-4">
                <p className="text-xs text-gray-500">
                    Showing <span className="font-medium text-gray-300">{(currentPage - 1) * pageSize + 1}</span> to <span className="font-medium text-gray-300">{Math.min(currentPage * pageSize, totalItems)}</span> of <span className="font-medium text-gray-300">{totalItems}</span>
                </p>

                <select
                    value={pageSize}
                    onChange={(e) => onPageSizeChange(Number(e.target.value))}
                    className="bg-gray-800 text-xs text-gray-300 border-none rounded px-2 py-1 focus:ring-1 focus:ring-blue-500 cursor-pointer"
                >
                    <option value={10}>10 / page</option>
                    <option value={20}>20 / page</option>
                    <option value={50}>50 / page</option>
                </select>
            </div>

            <div className="flex items-center gap-2">
                <button
                    onClick={() => onPageChange(currentPage - 1)}
                    disabled={currentPage === 1}
                    className="p-2 rounded-lg bg-gray-800 text-gray-400 hover:text-white hover:bg-gray-700 disabled:opacity-50 disabled:cursor-not-allowed transition"
                    title="Previous Page"
                >
                    <ChevronLeft size={16} />
                </button>
                <div className="text-sm font-medium text-gray-400 px-2 min-w-[80px] text-center">
                    Page {currentPage} of {Math.max(1, totalPages)}
                </div>
                <button
                    onClick={() => onPageChange(currentPage + 1)}
                    disabled={currentPage === totalPages || totalPages === 0}
                    className="p-2 rounded-lg bg-gray-800 text-gray-400 hover:text-white hover:bg-gray-700 disabled:opacity-50 disabled:cursor-not-allowed transition"
                    title="Next Page"
                >
                    <ChevronRight size={16} />
                </button>
            </div>
        </div>
    );
};
